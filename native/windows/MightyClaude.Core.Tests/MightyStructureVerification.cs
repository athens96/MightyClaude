using System.Globalization;
using System.Text;
using System.Text.Json;
using MightyClaude.Core;
using Fold = MightyClaude.Core.MightyStructure.Fold;
using Page = MightyClaude.Core.MightyStructureFollow.Page;
using Scroll = MightyClaude.Core.MightyStructureFollow.Scroll;
using Step = MightyClaude.Core.MightyStructure.Step;

/// <summary>
/// The Mighty structure view against the checks of the Mac's MightyStructureTests, MightyStructurePanTests and
/// MightyStructureFollowTests: each request's phase, the phase columns' states, a free pane's request → run → result rows,
/// the cards' texts and caps, the sub-agent clusters, folding, panning and following.
/// </summary>
internal static class MightyStructureVerification
{
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private const string Stamp = "2026-10-12T00:00:00.000Z";

    internal static MightyGraphRun Run(string id, string? input = null, string status = "completed", MightyGraphAgent[]? agents = null, string? final = null, string? source = null)
    {
        var run = new MightyGraphRun { Id = id, Input = input ?? "do " + id, Status = status, Agents = [.. agents ?? []], SourceRunID = source, FinalOutput = final, Provider = "claude" };
        MightyGraphSupport.RefreshResult(run); return run;
    }
    internal static MightyGraphAgent Agent(string id, string? parent = null, string status = "completed", string? kind = null) =>
        new() { Id = id, ParentID = parent, Title = "agent " + id, Status = status, Kind = kind };
    internal static PlanRecord Record(string id, string graphRunId, string outcome) =>
        new(id, "channel-" + id, "# plan", Stamp, "2026-10-12T00:01:00.000Z", outcome, GraphRunId: graphRunId);
    internal static LogEntry Said(string id, string text) => new(id, "assistant", text, Stamp);
    internal static LogEntry Tool(string id, string summary, string kind = "read", string state = "completed") =>
        new(id, "system", summary, Stamp, Activity: new("act-" + id, "claude", kind, state, summary));

    private static readonly Lazy<IReadOnlyList<RegisteredStyle>> Bundled = new(() =>
    {
        var root = Verification.Temp();
        try { return StyleRegistry.Load(Path.Combine(root, "profile"), Path.Combine(root, "workspace")).Styles; }
        finally { Directory.Delete(root, true); }
    });
    internal static StyleEvaluator Style(string id) => Bundled.Value.Single(s => s.Id == id).Evaluator;
    internal static StylePhase Phase(StyleEvaluator style, string id) => style.Manifest.Phases.Single(p => p.Id == id);

    private static string Cards(MightyStructure.Shape shape) => string.Join("|", shape.Columns!.Select(c => string.Join(",", c.Items.Select(i => i.RunId))));
    private static string Steps(MightyStructure.Shape shape) => string.Join(",", shape.Columns!.Select(c => MightyStructure.Word(c.Step)));
    private static int Bytes(string? text) => text is null ? int.MaxValue : Encoding.UTF8.GetByteCount(text);

    internal static Task PhaseAssignment()
    {
        var flow = Style("ouroboros");
        var named = new[]
        {
            Run("r1", "그냥 시작해줘"),            // nothing named: the style's default
            Run("r2", "ooo interview 할 일 앱"),   // an action with a phase
            Run("r3", "좀 더 물어봐"),             // free text: the previous run's
            Run("r4", "ooo status"),             // a phase-less action never moves the flow
            Run("r5", "ooo seed"),
            Run("r6", "ooo pm 다시"),              // an alias
        };
        Check(MightyStructure.PhaseIDs(named, flow).SequenceEqual(["goal", "interview", "interview", "interview", "seed", "interview"]), "runs take the recognised phase, else the previous, else the default: " + string.Join(",", MightyStructure.PhaseIDs(named, flow)));
        Check(MightyStructure.RecognisedPhase("ooo status", flow) is null && MightyStructure.RecognisedPhase("/ouroboros:evaluate", flow) == "evaluate", "a phase-less action names no phase; a slash action names its own");

        var plan = Style("claude-plan");
        var runs = new[] { Run("r1", "로그인 화면 계획 세워줘", source: "s1"), Run("r2", "테스트 돌려줘", source: "s2"), Run("r3", "이제 다음 화면", source: "s3"), Run("r4", "[계획] 새 기능", source: "s4") };
        // Sent back, then approved: the latest answer counts.
        var history = new[] { Record("p1", "s1", PlanOutcome.ApprovedAuto), Record("p3a", "s3", PlanOutcome.Revised), Record("p3b", "s3", PlanOutcome.ApprovedConfirm) };
        Check(MightyStructure.PhaseIDs(runs, plan, planHistory: history).SequenceEqual(["execute", "plan", "execute", "plan"]), "approved plans execute; a request with no plan answer is planning");
        Check(MightyStructure.PhaseIDs([Run("b1")], plan, planHistory: [Record("p", "b1", PlanOutcome.ApprovedConfirm)]).SequenceEqual(["execute"]), "a record is matched by the block id too");
        Check(MightyStructure.PhaseIDs(runs, plan, Phase(plan, "approve"), history).SequenceEqual(["execute", "plan", "execute", "plan"]), "the pane's phase never pulls a run into a stage its own records do not say");

        // Approved, then a new request without a plan: the pane's latest request has no plan answer, so it is planning.
        var later = MightyStructure.Make([Run("r1", source: "s1"), Run("r2", status: "running", source: "s2")], plan, Phase(plan, "plan"), planHistory: [Record("p1", "s1", PlanOutcome.ApprovedAuto)]);
        Check(Cards(later) == "r2||r1" && Steps(later) == "current,pending,done", "claude-plan: approved, then a new request: " + Cards(later) + " " + Steps(later));
        var executing = MightyStructure.Make([Run("r1", status: "running", source: "s1")], plan, Phase(plan, "execute"), planHistory: [Record("p1", "s1", PlanOutcome.ApprovedAuto)]);
        Check(Cards(executing) == "||r1" && Steps(executing) == "done,done,current", "claude-plan: executing after approval: " + Cards(executing) + " " + Steps(executing));
        // The revised plan's run asked again; that plan waits for an answer.
        var again = new[] { Run("r0", source: "s0"), Run("r1", status: "running", source: "s1") };
        var answered = new[] { Record("p0", "s0", PlanOutcome.ApprovedAuto), Record("p1", "s1", PlanOutcome.Revised) };
        var waiting = MightyStructure.Make(again, plan, Phase(plan, "approve"), planHistory: answered, pendingPlanRunID: "s1");
        Check(Cards(waiting) == "|r1|r0" && Steps(waiting) == "done,current,done", "claude-plan: sent back, then waiting again: " + Cards(waiting) + " " + Steps(waiting));
        // A pending plan whose run has drawn no block yet moves no card: the waiting column is current and empty until it does.
        var early = MightyStructure.Make([again[0]], plan, Phase(plan, "approve"), planHistory: answered, pendingPlanRunID: "s-new");
        Check(Cards(early) == "||r0" && Steps(early) == "done,current,done", "claude-plan: a pending plan with no block yet: " + Cards(early) + " " + Steps(early));

        var fresh = MightyStructure.Make([Run("r1", source: "s1")], plan, Phase(plan, "plan"), startingNew: true, planHistory: [Record("p1", "s1", PlanOutcome.ApprovedAuto)]);
        Check(Cards(fresh) == "||r1" && Steps(fresh) == "current,pending,done", "starting anew leaves a finished card in its done column: " + Cards(fresh) + " " + Steps(fresh));
        // A recognised-action style: the start phase is current, the requests stay where they were.
        var restart = MightyStructure.Make([Run("r1", "ooo interview 앱"), Run("r2", "ooo seed")], flow, Phase(flow, "goal"), startingNew: true);
        Check(Cards(restart) == "|r1|r2|||" && Steps(restart) == "current,done,done,pending,pending,pending", "starting anew in a recognised-action style: " + Cards(restart) + " " + Steps(restart));
        Check(MightyStructure.PhaseIDs([Run("r1", "ooo seed", source: "s1"), Run("r2", source: "s2")], flow, planHistory: [Record("p1", "s1", PlanOutcome.ApprovedAuto)]).SequenceEqual(["seed", "seed"]), "plan records only matter to a plan-state style");
        return Task.CompletedTask;
    }

    internal static Task ColumnsAndFreeRows()
    {
        var flow = Style("ouroboros");
        var newest = MightyStructure.Make([Run("r1", "ooo interview 앱"), Run("r2", "더 물어봐"), Run("r3", "ooo seed")], flow, Phase(flow, "run"));
        Check(newest.Columns!.Select(c => c.Id).SequenceEqual(["goal", "interview", "seed", "run", "evaluate", "evolve"]), "one column per phase, in order");
        // The pane stands in run (a state source may have moved it on): the newest card goes there, so the bar and the cards agree.
        Check(Cards(newest) == "|r1,r2||r3||" && Steps(newest) == "done,done,done,current,pending,pending", "the newest card sits in the pane's phase: " + Cards(newest) + " " + Steps(newest));
        Check(newest.Columns![1].Items.Select(i => i.Ordinal).SequenceEqual([1, 2]), "oldest first under their phase");
        // The flow went back to the interview after a seed.
        var back = MightyStructure.Make([Run("r1", "ooo seed"), Run("r2", "ooo interview 다시")], flow, Phase(flow, "interview"));
        Check(Cards(back) == "|r2|r1|||" && Steps(back) == "done,current,done,pending,pending,pending", "a column holding cards is never pending: " + Cards(back) + " " + Steps(back));
        var powers = Style("superpowers");
        Check(Steps(MightyStructure.Make([Run("r1", "/superpowers:brainstorming 아이디어"), Run("r2", "/superpowers:writing-plans")], powers, null)) == "done,current,pending,pending", "with no current phase the newest request's phase is current");

        var runs = new[]
        {
            Run("running", status: "running"), Run("done", final: "끝났어요\n두 번째 줄"), Run("failed", status: "error"), Run("halted", status: "stopped"),
            // The request settled but a block still runs: the run is not over yet.
            Run("tail", agents: [Agent("bg", status: "running", kind: "task")]),
        };
        // A style without phases is drawn as a free pane, as no style at all is.
        Check(MightyStructure.Make(runs, Style("paperthin"), null) == MightyStructure.Make(runs, null, null), "a style without phases draws a free pane");
        var rows = MightyStructure.Make(runs, null, null).Rows!;
        Check(rows.Select(r => r.Item.RunId).SequenceEqual(["running", "done", "failed", "halted", "tail"]) && rows.All(r => r.Request == Step.Done), "one row per request, oldest first, each with its request done");
        Check(rows.Select(r => r.Execution).SequenceEqual([Step.Current, Step.Done, Step.Error, Step.Stopped, Step.Current]), "the run goes on until every block settled, unless the request failed or was stopped");
        Check(rows.Select(r => r.Result).SequenceEqual([Step.Pending, Step.Done, Step.Error, Step.Stopped, Step.Pending]), "the result waits until the request finished");
        Check(rows.Select(r => r.Item.Status).SequenceEqual(["running", "completed", "error", "stopped", "completed"]), "statuses are bucketed as a block's are");
        Check(rows[1].Item.Result == "끝났어요 두 번째 줄" && rows[0].Item.Result is null && rows[1].Item.Input == "do done", "the result on one line, and only once the request finished");
        Check(rows[1].Item.NodeId == MightyGraphBlockSize.NodeId("done", "request") && rows[1].Item.ExecutionNodeId == MightyGraphBlockSize.NodeId("done", MightyStructure.ExecutionSuffix), "a card names its request's and its run's diagram nodes");

        Check(MightyStructure.Make([], null, null) == MightyStructure.Shape.Free([]), "an empty pane has no requests");
        var idle = MightyStructure.Make([], flow, null).Columns!;
        Check(idle.Count == 6 && idle.All(c => c.Items.Count == 0 && c.Step == Step.Pending), "a style's columns show before the first request, all pending");
        Check(Steps(MightyStructure.Make([], flow, Phase(flow, "goal"))) == "current,pending,pending,pending,pending,pending", "the pane's phase is current before the first request");
        return Task.CompletedTask;
    }

    internal static Task CardsShowTheWorkInPlace()
    {
        var work = Run("r", "화면을 고쳐줘", "running"); work.RootEntries = [Said("m1", "읽어 볼게요"), Tool("t1", "a.swift")];
        var card = MightyStructure.ItemFor(work, 0);
        Check(card.Request?.Text == "화면을 고쳐줘" && card.Input == "화면을 고쳐줘", "the request part is the input alone");
        Check(card.Message?.Text == "읽어 볼게요" && card.Activity.Select(a => a.Id).SequenceEqual(["t1"]) && card.Status == "running", "the run part reads the main block's records");
        Check(card.ExecutionNodeId == MightyGraphBlockSize.NodeId("r", "execution") && card.ExecutionNodeId != card.NodeId, "the run part stands for its own diagram node");
        var branched = MightyStructure.ItemFor(Run("r", "화면을 고쳐줘", "running", [Agent("a")]), 0);
        var laid = MightyGraphLayout.Make([Run("r", "화면을 고쳐줘", "running", [Agent("a")])], "", true, new HashSet<string>());
        Check(laid.Nodes.Any(n => n.Id == branched.NodeId) && laid.Nodes.Any(n => n.Id == branched.Clusters[0].Nodes[0].NodeId), "the request and its sub-agent are nodes the diagram lays out");
        var fresh = MightyStructure.ItemFor(Run("n", status: "running"), 0);
        Check(fresh.Message is null && fresh.Activity.Count == 0 && !fresh.Finished, "a request with nothing yet still has its run part, waiting");

        var counted = Run("r", agents: [Agent("a"), Agent("t", kind: "task"), Agent("s", kind: "steer")]);
        counted.RootEntries = [Tool("x", "README.md"), new("y", "system", "turn", Stamp, Activity: new("y", "claude", "turn", "completed", "turn"))];
        counted.Agents[0].Entries = [Tool("z", "a.swift", "edit", "running")];
        // A steer's records are not a sub-agent's work.
        counted.Agents[2].Entries = [Tool("w", "b.swift")];
        Check(MightyStructure.ItemFor(counted, 0).Tally == new MightyStructure.Tally(1, 2), "the tally counts sub-agents and tool calls");

        var live = Run("r", "로그인 화면을 고쳐줘\n두 번째 줄", "running");
        live.RootEntries =
        [
            Said("m1", "먼저 읽어 볼게요"), Tool("t1", "a.swift"),
            new("turn", "system", "turn", Stamp, Activity: new("turn", "claude", "turn", "running", "turn")),
            Tool("t2", "login", "search"), Tool("t3", "b.swift", "edit"), Tool("t4", "swift test\n--filter Login", "command", "running"),
            Said("m2", "고치는 중이에요\n테스트를 돌립니다"), Tool("t5", "", "web"), Tool("t6", "c.swift"),
            // A question's raw form is not a reply.
            Said("q", "{\"questions\":[{\"header\":\"H\",\"question\":\"Q?\",\"multiSelect\":false,\"options\":[{\"label\":\"A\",\"description\":\"\"},{\"label\":\"B\",\"description\":\"\"}]}]}"),
        ];
        var running = MightyStructure.ItemFor(live, 0);
        Check(running.Request?.Text == "로그인 화면을 고쳐줘\n두 번째 줄" && running.Message is { Text: "고치는 중이에요\n테스트를 돌립니다", Cut: false } message && !message.Folds(MightyStructure.MessageLines, 20), "a running request shows its latest message");
        // The last five tool calls, oldest first, on one line each; the turn row and a call with no words (nor a tool name) are not shown.
        Check(running.Activity.Select(a => a.Id).SequenceEqual(["t1", "t2", "t3", "t4", "t6"]) && running.Activity.Select(a => a.Kind).SequenceEqual(["read", "search", "edit", "command", "read"]), "a running request shows its last five tool calls");
        Check(running.Activity[3] is { Text: "swift test --filter Login", State: "running" } && running.Answer is null && running.Result is null, "a tool call is its summary on one line; a running request has no answer");

        var answer = "# 끝났어요\n\n- 로그인 수정\n- 테스트 통과"; var done = Run("r", final: answer);
        var finished = MightyStructure.ItemFor(done, 0);
        // RefreshResult put the answer in the main block too.
        Check(done.RootEntries[^1].Text == answer && finished.Answer is { } shown && shown.Text == answer && shown.Preview == answer && finished.Message is null, "a finished request shows its result once");
        var failed = Run("f", status: "error"); failed.RootEntries = [Said("m", "권한이 없어요")];
        Check(MightyStructure.ItemFor(failed, 0) is { Answer: null, Message.Text: "권한이 없어요" }, "a failed request has no answer; its last reply stays its message");

        var reviewer = new MightyGraphAgent { Id = "a", Title = "검토", Input = "변경을 검토해 줘\n특히 보안", Status = "running", ResponseRecords = [new("x", null, new GraphTokenUsage(), []), new("y", "claude-opus-4-1", new GraphTokenUsage(), [])] };
        reviewer.Entries = [Tool("a1", "a.swift"), Said("o1", "문제 없음"), Tool("a2", "b.swift"), Tool("a3", "c.swift"), Tool("a4", "make", "command")];
        var build = new MightyGraphAgent { Id = "t", ParentID = "a", Title = "빌드", Input = "make all", Status = "completed", Kind = "task", Entries = [Said("t-answer", "빌드 성공")] };
        var nodes = MightyStructure.Clusters(Run("r", status: "running", agents: [reviewer, build])).SelectMany(c => c.Nodes).ToList();
        Check(nodes.Select(n => n.AgentId).SequenceEqual(["a", "t"]) && nodes[0].Prompt is { Text: "변경을 검토해 줘\n특히 보안" } prompt && !prompt.Folds(MightyStructure.PromptLines, 20), "a sub-agent carries its prompt");
        Check(nodes[0].Output?.Text == "문제 없음" && nodes[0].Activity.Select(a => a.Id).SequenceEqual(["a2", "a3", "a4"]) && nodes[0].Model == "claude-opus-4-1", "a sub-agent carries its output, its last three tool calls and its model");
        Check(nodes[1].Prompt?.Text == "make all" && nodes[1].Output?.Text == "빌드 성공" && nodes[1].Activity.Count == 0 && nodes[1].Model is null, "a task carries its command and its answer");
        return Task.CompletedTask;
    }

    internal static Task TextsAreCappedAndFolded()
    {
        var syllables = new string('가', 3_000);   // 9,000 bytes
        var request = MightyStructure.ExcerptOf(syllables, MightyStructure.RequestBytes, MightyStructure.RequestLines);
        Check(request is not null && Bytes(request.Text) <= MightyStructure.RequestBytes && request.Text.EndsWith('…') && request.Cut, "a long request is cut at its cap and marked");
        // A long line is cut in the preview, so a folded card never lays out the whole text.
        Check(new StringInfo(request!.Preview).LengthInTextElements <= MightyStructure.PreviewLineCharacters, "a long line is cut in the preview");
        var twenty = string.Join("\n", Enumerable.Range(1, 20).Select(i => "line " + i));
        var folded = MightyStructure.ExcerptOf(twenty, MightyStructure.MessageBytes, MightyStructure.MessageLines);
        Check(folded is { Cut: true } && folded.Text == twenty && folded.Preview == string.Join("\n", Enumerable.Range(1, 8).Select(i => "line " + i)), "the first lines fold, the text keeps them all");
        Check(MightyStructure.ExcerptOf("짧은 글", 100, 2) == new MightyStructure.Excerpt("짧은 글", "짧은 글") && MightyStructure.ExcerptOf(" \n\u001b[0m ", 100, 2) is null, "short text fits as it is; nothing but blanks or controls is nothing");
        // The Windows composer ends a typed line with a bare CR, a pasted one with CR LF: both are lines, on the desktop and on the phone.
        var typed = string.Join("\r", Enumerable.Range(1, 12).Select(i => "line " + i)); var read = typed.Replace('\r', '\n');
        var asked = MightyStructure.ExcerptOf(typed, MightyStructure.RequestBytes, MightyStructure.RequestLines);
        Check(asked is { Cut: true } && asked.Text == read && asked.Preview == string.Join("\n", Enumerable.Range(1, 10).Select(i => "line " + i)), "a request typed with bare carriage returns folds at its ten lines");
        Check(MightyStructure.ExcerptOf("a\r\nb\rc\nd", 100, 3) == new MightyStructure.Excerpt("a\nb\nc\nd", "a\nb\nc"), "CR LF, CR and LF each end one line");
        Check(MightyStructure.ExcerptOf("```\r\ncode\r\nmore", 100, 2, markdown: true)?.Preview == "```\ncode\n```", "a fence is found and closed in a text with carriage returns");
        Check(MobileStructure.ItemFor(MightyStructure.ItemFor(Run("typed", typed), 0), null).Request == read, "the phone is sent the same lines");
        // The result and the sub-agent texts take their own caps.
        var big =string.Concat(Enumerable.Repeat("result ", 5_000));
        var item = MightyStructure.ItemFor(Run("r", syllables, final: big), 0);
        Check(Bytes(item.Request?.Text) <= MightyStructure.RequestBytes && item.Answer is { Cut: true } answer && Bytes(answer.Text) <= MightyStructure.AnswerBytes && answer.Text.EndsWith('…'), "the request and the answer take their own caps");
        var node = MightyStructure.Clusters(Run("r2", agents: [new MightyGraphAgent { Id = "a", Input = syllables, Status = "completed", Entries = [Said("o", big)] }]))[0].Nodes[0];
        Check(Bytes(node.Prompt?.Text) <= MightyStructure.PromptBytes && Bytes(node.Output?.Text) <= MightyStructure.OutputBytes, "a sub-agent's prompt and output take their own caps");

        // A node six levels deep in the narrowest column has 196pt for its text.
        var width = MightyStructure.NodeTextWidth(MightyStructure.MinimumColumnWidth, 6);
        Check(width == 196 && MightyStructure.NodeTextWidth(MightyStructure.MinimumColumnWidth, 60) == width, "a deep node's text width stops shrinking at the indent cap: " + width);
        // About 17 Hangul syllables fit a 196pt line at 11pt; words wrap whole, so fewer are counted.
        var perLine = MightyStructure.CharactersPerLine(width, 11);
        Check(perLine <= 17, "a wrapped line is counted as only partly filled: " + perLine);
        // 120 syllables on one line take 8 lines of 17: past the 6 an output shows folded.
        var output = MightyStructure.ExcerptOf(string.Concat(Enumerable.Repeat("가나다라마 ", 20)), MightyStructure.OutputBytes, MightyStructure.OutputLines);
        Check(output is { Cut: false } && output.Folds(MightyStructure.OutputLines, perLine), "a text that wraps past its lines folds without being cut");
        // Any text that would wrap past its lines at the real width folds.
        for (var count = 1; count <= 200; count += 7)
            Check((count + 16) / 17 <= 6 || MightyStructure.ExcerptOf(new string('가', count), 4_096, 6)?.Folds(6, perLine) == true, count + " syllables must fold");
        // The prompt's two lines at its smaller size, the same way.
        Check(MightyStructure.ExcerptOf(string.Concat(Enumerable.Repeat("검토", 20)), MightyStructure.PromptBytes, MightyStructure.PromptLines)?.Folds(MightyStructure.PromptLines, MightyStructure.CharactersPerLine(width, 10.5)) == true, "a prompt folds at its own size");

        var lines = new[] { "# 결과", "", "```swift" }.Concat(Enumerable.Range(1, 20).Select(i => $"let value{i} = {i}")).Concat(["```", "끝"]).ToArray();
        var fenced = MightyStructure.ExcerptOf(string.Join("\n", lines), MightyStructure.AnswerBytes, MightyStructure.AnswerLines, markdown: true)!;
        // The first fifteen lines whole, and the fence they leave open closed.
        Check(fenced.Cut && fenced.Preview == string.Join("\n", lines.Take(15).Append("```")) && MightyStructure.OpenFence(fenced.Preview) is null, "a folded answer keeps whole lines and closes its code fence");
        Check(MightyStructure.OpenFence("~~~~\ncode") == "~~~~" && MightyStructure.OpenFence("```\ncode\n```") is null, "a tilde fence is closed with its own mark; a closed one is left alone");
        // Lines stop at the preview's budget rather than being cut mid-line.
        var wide = MightyStructure.ExcerptOf(string.Join("\n", Enumerable.Range(1, 15).Select(i => i + " " + new string('x', 300))), MightyStructure.AnswerBytes, MightyStructure.AnswerLines, markdown: true)!;
        Check(wide.Preview.Split('\n').All(line => line.Length is 302 or 303) && wide.Preview.Length <= MightyStructure.AnswerLines * MightyStructure.PreviewLineCharacters, "a Markdown preview stops at whole lines within its budget");
        Check(MightyStructure.ExcerptOf("**됐어요**", 100, 15, markdown: true) is { Cut: false }, "a short answer is shown whole, with no toggle");

        // Three columns in 1,000pt with 12pt gaps: (1000 - 24) / 3. Too narrow: the minimum, and the view scrolls sideways.
        Check(MightyStructure.ColumnWidth(1_000, 3, 12) == 325 && MightyStructure.ColumnWidth(700, 6, 12) == MightyStructure.MinimumColumnWidth && MightyStructure.ColumnWidth(2_000, 0, 12) == MightyStructure.MinimumColumnWidth, "columns share the pane's width down to the minimum");
        return Task.CompletedTask;
    }

    internal static Task ClustersAreTheRootTrees()
    {
        var agents = new[]
        {
            Agent("a"), Agent("b", "a", "running"), Agent("c", "b"), Agent("s", kind: "steer"),
            Agent("d", "ghost"),                 // an unknown parent is a root
            Agent("q", "a", kind: "question"), Agent("t", "a", kind: "task"),
            Agent("e", "e"),                     // its own parent is a root
            Agent("k", kind: "compact"),
            Agent("u", "k"),                     // under a left-out block: a root
        };
        var clusters = MightyStructure.Clusters(Run("r", agents: agents));
        Check(string.Join("|", clusters.Select(c => string.Join(",", c.Nodes.Select(n => n.AgentId)))) == "a,b,c,t|d|e|u", "one cluster per root tree, flattened depth-first");
        Check(clusters[0].Nodes.Select(n => n.Depth).SequenceEqual([0, 1, 2, 1]) && clusters[0].Nodes.Select(n => n.Kind).SequenceEqual(["agent", "agent", "agent", "task"]) && clusters[0].Nodes[1].Status == "running", "a node carries its depth, kind and status");
        Check(clusters[0].Nodes[0].NodeId == MightyGraphBlockSize.NodeId("r", "agent:a") && clusters[0].Nodes[0].Title == "agent a" && clusters[0].Id == clusters[0].Nodes[0].NodeId, "a node names its diagram block; a cluster is named by its root");

        var repeated = new[] { Agent("a"), Agent("b", "a"), Agent("a", "b"), Agent("a") };
        var kept = MightyStructure.Clusters(Run("r", agents: repeated));
        Check(kept.Count == 1 && kept[0].Nodes.Select(n => n.AgentIndex).SequenceEqual([0, 1]) && MightyStructure.ItemFor(Run("r", agents: repeated), 0).Tally.Agents == 2, "a repeated block id is kept at its first place");

        var cycle = MightyStructure.Clusters(Run("r", agents: [Agent("x", "y"), Agent("y", "x")]));
        Check(cycle.Count == 1 && cycle[0].Nodes.Select(n => n.AgentId).SequenceEqual(["x", "y"]) && cycle[0].Nodes.Select(n => n.Depth).SequenceEqual([0, 1]), "a cycle shows each block once");

        var chain = MightyStructure.Clusters(Run("r", agents: Enumerable.Range(0, 70).Select(i => Agent("n" + i, i == 0 ? null : "n" + (i - 1))).ToArray()));
        Check(chain.Count == 2 && chain[0].Nodes.Count == MightyStructure.MaximumDepth && chain[0].Nodes[^1].Depth == MightyStructure.MaximumDepth - 1, "a deep chain stops at the cap");
        Check(chain[1].Nodes.Select(n => n.AgentId).SequenceEqual(Enumerable.Range(64, 6).Select(i => "n" + i)) && chain[1].Nodes[0].Depth == 0 && chain.Sum(c => c.Nodes.Count) == 70, "what the cap left out goes on in a new cluster");
        return Task.CompletedTask;
    }

    internal static Task SignatureAndItemCache()
    {
        var style = Style("ouroboros"); var goal = Phase(style, "goal");
        MightyGraphRun Live(string agentStatus = "running") => Run("r", status: "running", agents: [Agent("a", status: agentStatus)]);
        var run = Live(); var before = new MightyStructure.Signature([run], style, goal);
        Check(before == new MightyStructure.Signature([run], style, goal), "the signature of the same pane is the same");
        // A streamed line may add a tool call.
        run.Agents[0].Entries.Add(new("added", "system", "read", Stamp));
        Check(new MightyStructure.Signature([run], style, goal) != before && before != new MightyStructure.Signature([Live("completed")], style, goal), "a new record or a block's status moves the signature");
        Check(before != new MightyStructure.Signature([Live()], style, goal, startingNew: true) && before != new MightyStructure.Signature([Live()], style, goal, pendingPlanRunID: "s"), "starting anew or a pending plan moves the signature");

        var grown = Live(); grown.RootEntries = [Said("m", "읽는 중")]; grown.Agents[0].Entries = [Tool("t", "a.swift", state: "running")];
        var still = new MightyStructure.Signature([grown], null, null);
        // A reply that grew by a few lines keeps its record and the count.
        var longer = grown.Copy(); longer.RootEntries[0] = longer.RootEntries[0] with { Text = "읽는 중" + string.Concat(Enumerable.Repeat("\n이제 고칩니다", 20)) };
        Check(new MightyStructure.Signature([longer], null, null) != still, "a reply that grew in place moves the signature");
        // A tool call that finished, and a sub-agent's prompt that arrived.
        var settled = grown.Copy(); settled.Agents[0].Entries[0] = settled.Agents[0].Entries[0] with { Activity = settled.Agents[0].Entries[0].Activity! with { State = "completed" } };
        var prompted = grown.Copy(); prompted.Agents[0].Input = "검토해 줘";
        Check(new MightyStructure.Signature([settled], null, null) != still && new MightyStructure.Signature([prompted], null, null) != still, "a tool call that finished or a prompt that arrived moves the signature");

        var stream = Run("r", status: "running");
        stream.RootEntries = [Tool("old", "swift build", state: "running"), .. Enumerable.Range(1, 20).Select(i => Tool("t" + i, $"f{i}.swift")), Said("m", "가")];
        var quiet = new MightyStructure.Signature([stream], null, null);
        // A few more words in the streamed reply: nothing to rebuild yet. A few lines more: the card is built again.
        var words = stream.Copy(); words.RootEntries[^1] = words.RootEntries[^1] with { Text = "가 나다" };
        var more = stream.Copy(); more.RootEntries[^1] = more.RootEntries[^1] with { Text = string.Concat(Enumerable.Repeat("한 줄의 글입니다\n", 12)) };
        Check(new MightyStructure.Signature([words], null, null) == quiet && new MightyStructure.Signature([more], null, null) != quiet, "the signature moves every few lines, not every word");
        // A tool call older than the newest records finishing in place.
        var older = stream.Copy(); older.RootEntries[0] = older.RootEntries[0] with { Activity = older.RootEntries[0].Activity! with { State = "completed" } };
        Check(new MightyStructure.Signature([older], null, null) != quiet, "an older tool call finishing in place moves the signature");

        var runs = new List<MightyGraphRun> { Run("r1", final: "첫 결과"), Run("r2", status: "running") };
        var cache = new MightyStructure.ItemCache();
        Check(cache.Items(runs, new(runs, null, null)).SequenceEqual(runs.Select(MightyStructure.ItemFor)), "the cache builds every card the first time");
        runs[1].RootEntries.Add(Said("m", "진행 중"));
        var second = cache.Items(runs, new(runs, null, null));
        Check(second[1].Message?.Text == "진행 중" && second.SequenceEqual(runs.Select(MightyStructure.ItemFor)), "the cache rebuilds the request that moved");
        Check(ReferenceEquals(second[0], cache.Items(runs, new(runs, null, null))[0]), "a card that kept its place is not copied");
        // The oldest request trimmed: a kept card takes its new number.
        var trimmed = runs.Skip(1).ToList(); var third = cache.Items(trimmed, new(trimmed, null, null));
        Check(third.SequenceEqual([MightyStructure.ItemFor(trimmed[0], 0)]) && third[0].Ordinal == 1, "a kept card takes its new number after a trim");
        Check(MightyStructure.Make(trimmed, null, null, items: third) == MightyStructure.Make(trimmed, null, null), "the shape built from cached cards is the shape built from scratch");

        // A block without a title is named in the app's language: a live language switch moves the signature and reads the cards again.
        var saved = Locale.LanguagePreference;
        try
        {
            Locale.LanguagePreference = "ko"; Locale.ResetCache();
            var untitled = new List<MightyGraphRun> { Run("u", agents: [new MightyGraphAgent { Id = "a", Status = "completed" }]) };
            var named = new MightyStructure.ItemCache(); var korean = new MightyStructure.Signature(untitled, null, null);
            var title = named.Items(untitled, korean)[0].Clusters[0].Nodes[0].Title;
            Locale.LanguagePreference = "en"; Locale.ResetCache();
            var english = new MightyStructure.Signature(untitled, null, null);
            Check(title == Locale.Catalogue("ko")["graph.block.agent"] && english != korean && named.Items(untitled, english)[0].Clusters[0].Nodes[0].Title == Locale.Catalogue("en")["graph.block.agent"], "another language names an untitled block again");
        }
        finally { Locale.LanguagePreference = saved; Locale.ResetCache(); }
        return Task.CompletedTask;
    }

    internal static Task SavedChoiceSurvivesARestart()
    {
        var pane = new RunSession { Id = "s", WorkspaceId = "w", AgentViewMode = "mighty", Draft = "초안" };
        var chosen = MightyTimeline.ApplyMode(pane, "structure");
        Check(chosen.GraphViewMode == "structure" && MightyTimeline.Mode(chosen) == "structure" && chosen.Draft == pane.Draft && chosen.AgentViewMode == "mighty", "the structure choice changes only the pane's graph presentation");
        var json = JsonSerializer.Serialize(chosen, Wire.Json);
        Check(json.Contains("\"graphViewMode\":\"structure\"", StringComparison.Ordinal) && JsonSerializer.Deserialize<RunSession>(json, Wire.Json)!.GraphViewMode == "structure", "the structure choice round-trips as its word");
        // A build that does not know a word reads the pane as the diagram.
        Check(MightyTimeline.Mode(JsonSerializer.Deserialize<RunSession>("{\"id\":\"s\",\"workspaceId\":\"w\",\"graphViewMode\":\"structure2\"}", Wire.Json)!) == "diagram", "an unknown word reads as the diagram");
        var directory = Verification.Temp();
        try
        {
            var restored = StateStore.Normalize(new AppSnapshot { Workspaces = [new Workspace { Id = "w", Path = directory }], Sessions = [chosen, chosen with { Id = "sh", Kind = "shell" }] }, true);
            Check(restored.Sessions.Single(s => s.Id == "s").GraphViewMode == "structure" && restored.Sessions.Single(s => s.Id == "sh").GraphViewMode is null, "a restart keeps the structure choice on an agent pane and drops it from a shell");
        }
        finally { Directory.Delete(directory, true); }
        return Task.CompletedTask;
    }

    internal static Task FinishedRequestsFold()
    {
        var none = new HashSet<string>();
        var running = MightyStructure.ItemFor(Run("a", status: "running"), 0);
        Check(!Fold.IsFolded(running, none) && !Fold.IsFolded(MightyStructure.ItemFor(Run("w", status: "waiting"), 1), none), "a running or waiting request is always open");
        // The same request finishes: it folds by itself, with no event of its own.
        foreach (var status in new[] { "completed", "error", "stopped" }) Check(Fold.IsFolded(MightyStructure.ItemFor(Run("a", status: status), 0), none), "a finished request folds: " + status);
        Check(!Fold.IsFolded(MightyStructure.ItemFor(Run("b", agents: [Agent("x", status: "running")]), 0), none), "a request whose sub-agent still runs is not finished yet");
        var items = new[] { running, MightyStructure.ItemFor(Run("c"), 1), MightyStructure.ItemFor(Run("d", status: "error"), 2) };
        Check(Fold.Folded(items, new HashSet<string> { "d" }).SetEquals(["c"]), "the folded cards are the finished ones not opened by hand");

        var finished = MightyStructure.ItemFor(Run("a"), 0); var opened = Fold.Toggled(finished, none);
        Check(opened.SetEquals(["a"]) && !Fold.IsFolded(finished, opened), "a card opened by hand stays open");
        // A later update of the same finished request keeps the user's choice.
        Check(!Fold.IsFolded(MightyStructure.ItemFor(Run("a", final: "다시 끝났습니다"), 0), opened), "a later update keeps the user's choice");
        var closed = Fold.Toggled(finished, opened);
        Check(closed.Count == 0 && Fold.IsFolded(finished, closed), "a card folds back by hand");
        // A running card has no fold control: toggling it changes nothing, and it still folds when it finishes.
        var live = MightyStructure.ItemFor(Run("b", status: "running"), 1);
        Check(Fold.Toggled(live, none).Count == 0 && Fold.IsFolded(MightyStructure.ItemFor(Run("b"), 1), Fold.Toggled(live, none)), "a running card has no fold control");

        Check(Fold.ResultLine(MightyStructure.ItemFor(Run("a", final: "\n## **결론**: 끝났습니다\n\n- 둘째 줄"), 0)) == "결론: 끝났습니다", "a folded card shows the answer's first line without its Markdown marks");
        // A failed or stopped request says so instead of its last words.
        Check(Fold.ResultLine(MightyStructure.ItemFor(Run("e", status: "error", final: "실패 이유"), 0)) is null && Fold.ResultLine(MightyStructure.ItemFor(Run("s", status: "stopped"), 0)) is null && Fold.ResultLine(MightyStructure.ItemFor(Run("n"), 0)) is null, "a request that failed, stopped or has no answer has no result line");
        Check(Fold.FirstLine("```swift\n1. `let` x\n") == "let x" && Fold.FirstLine("> * 인용 목록") == "인용 목록" && Fold.FirstLine("  \n\t\n") is null, "the first line drops a fence's opening, a list's number, a quote and a list mark");
        return Task.CompletedTask;
    }

    internal static Task DraggingEmptySpacePans()
    {
        var minimum = (X: 0d, Y: 0d); var maximum = (X: 240d, Y: 1_800d);
        // The pointer moves 40 left and 70 down: the page shows 40 more to the right and 70 more above.
        Check(MightyStructurePan.Origin((100, 900), (300, 200), (260, 270), minimum, maximum) == (140, 830) && MightyStructurePan.Origin((100, 900), (300, 200), (300, 200), minimum, maximum) == (100, 900), "the grabbed point stays under the pointer");
        Check(MightyStructurePan.Origin((20, 1_750), (300, 300), (600, -500), minimum, maximum) == (0, 1_800) && MightyStructurePan.Origin((20, 1_750), (300, 300), (-900, 4_000), minimum, maximum) == (240, 0), "the page stops at its edges");
        Check(MightyStructurePan.Origin((0, 0), (300, 300), (400, 400), (-12, -8), maximum) == (-12, -8), "a page offset by its insets stops at its own limits");
        // Narrower than the pane: only y moves. A page with no valid range keeps its origin.
        Check(MightyStructurePan.Origin((0, 500), (0, 0), (-80, -60), (0, 0), (0, 1_000)) == (0, 560) && MightyStructurePan.Origin((5, 5), (0, 0), (-80, -60), (10, double.NaN), (0, 100)) == (5, 5), "an axis that cannot move keeps its origin");
        Check(MightyStructurePan.Scrollable((0, 0), (0, 1_000)) && !MightyStructurePan.Scrollable((0, 0), (0.2, 0.4)), "a page is scrollable when either axis can move");
        Check(MightyStructurePan.MayPan(MightyStructurePan.Target.Page, false, true), "an unmodified press on a movable page may pan");
        // Text selects, a scroll bar scrolls, an inner scroll view keeps its own scroll, and the composer or another pane is not this page.
        foreach (var target in new[] { MightyStructurePan.Target.TextOrControl, MightyStructurePan.Target.InnerScroll, MightyStructurePan.Target.Outside }) Check(!MightyStructurePan.MayPan(target, false, true), "only the page pans: " + target);
        Check(!MightyStructurePan.MayPan(MightyStructurePan.Target.Page, true, true) && !MightyStructurePan.MayPan(MightyStructurePan.Target.Page, false, false), "a modified press, or a page that cannot move, keeps the press");
        Check(!MightyStructurePan.IsDrag((100, 100), (100, 100)) && !MightyStructurePan.IsDrag((100, 100), (102, 98)) && MightyStructurePan.IsDrag((100, 100), (103, 100)) && MightyStructurePan.IsDrag((100, 100), (100, 96)), "a short press is a click, not a drag");

        // The page follows the newest card at its end, 2,000pt tall in a 600pt pane.
        var watch = new MightyStructureFollow.PageWatch();
        Page At(double origin, double document = 2_000, double newest = 1_992) => new(origin, 600, document, "b", newest);
        Check(watch.Observe(At(1_400), false) is null && watch.Following, "the page follows at its end");
        // The user drags the page down by 200pt: it moves up, a user move.
        var dragged = MightyStructurePan.Origin((0, 1_400), (50, 50), (50, 250), (0, 0), (0, 1_400));
        Check(dragged.Y == 1_200 && watch.Observe(At(dragged.Y), true) is null && !watch.Following, "a drag away stops following");
        // The newest card grows while the drag goes on and after it: nothing pulls the page back.
        Check(watch.Observe(At(dragged.Y, 2_300, 2_292), true) is null && watch.Observe(At(dragged.Y, 2_400, 2_392), false) is null, "a pan is never fought");
        // Dragged back to the newest card: following again, by the scroll rule.
        var back = MightyStructurePan.Origin(dragged, (50, 250), (50, -550), (0, 0), (0, 1_800));
        Check(back.Y == 1_800 && watch.Observe(At(back.Y, 2_400, 2_392), true) is null && watch.Following, "dragged back to the newest card, the page follows again");
        return Task.CompletedTask;
    }

    internal static Task ThePageFollowsTheNewestCard()
    {
        static Page At(double origin, double document, double? newest, double visible = 600, string? id = "b") => new(origin, visible, document, id, newest);
        // The newest card sits in a shorter column, 300pt above the end of the page; the reader scrolls all the way down.
        var watch = new MightyStructureFollow.PageWatch();
        watch.Observe(At(2_000, 3_000, 2_500), false);
        Check(watch.Observe(At(2_400, 3_000, 2_700), false) is null && watch.Following, "a reader at the true bottom still follows");
        // The newest card grows while staying above the viewport's bottom: nothing moves.
        Check(watch.Observe(At(2_400, 3_000, 2_760), false) is null && watch.Observe(At(2_400, 3_000, 2_960), false) is null, "a reader at the true bottom is never pulled up to the newest card");

        // A card that grew by more than the slack in one layout pass is still followed.
        watch = new(); watch.Observe(At(1_400, 2_000, 1_992), false);
        Check(watch.Following && watch.Observe(At(1_400, 2_300, 2_292), false) == Scroll.Bottom && watch.Following, "a growth that hides the newest card is followed in one pass");
        Check(watch.Observe(At(1_700, 2_300, 2_292), false) is null && watch.Following, "the scroll that lands at the bottom keeps following");

        watch = new(); watch.Observe(At(1_000, 3_000, 1_590), false);
        Check(watch.Following && watch.Observe(At(1_000, 3_000, 1_700), false) == new Scroll("b"), "a card in a shorter column is shown by its own bottom");

        watch = new(); watch.Observe(At(1_400, 2_000, 1_992), false);
        Check(watch.Observe(At(300, 2_000, 1_992), false) is null && !watch.Following, "a reader who scrolled up no longer follows");
        Check(watch.Observe(At(300, 2_400, 2_392), false) is null && watch.Observe(At(300, 2_400, 2_392, 560), false) is null, "a reader who scrolled up stays where they are");
        // Back at the newest card: following again.
        Check(watch.Observe(At(1_800, 2_400, 2_392, 560), false) is null && watch.Following, "a reader who comes back to the newest card follows again");

        // The page grows while the user's wheel is moving it up.
        watch = new(); watch.Observe(At(1_400, 2_000, 1_992), false);
        Check(watch.Observe(At(1_200, 2_300, 2_292), true) is null && !watch.Following, "a scroll in progress is never fought");

        watch = new(); watch.Observe(At(1_400, 2_000, 1_992), false);
        Check(watch.Observe(At(1_400, 2_000, 1_992, 560), false) == Scroll.Bottom, "a taller composer keeps the newest card above it");

        watch = new(); watch.Observe(At(1_400, 2_000, 1_992), false); watch.Observe(At(200, 2_000, 1_992), false);
        Check(!watch.Following && watch.NewRequest("c") == new Scroll("c") && watch.Following, "a new request is shown and followed from then");
        // The page lands short of it: still following, and shown again.
        Check(watch.Observe(At(1_500, 2_400, 2_392, id: "c"), false) == Scroll.Bottom && watch.Observe(At(1_800, 2_400, 2_392, id: "c"), false) is null && watch.Following, "a new request is shown again when the page lands short of it");

        watch = new(); watch.Observe(At(1_400, 2_000, 1_992), false); watch.Stop();
        Check(!watch.Following && watch.Observe(At(1_400, 2_300, 2_292), false) is null, "opening a text stops following");

        // The page opens at its end, where the newest card is.
        watch = new(); watch.Stop();
        Check(watch.Appeared("b") is null && watch.Observe(At(1_400, 2_000, 1_992), false) is null && watch.Following, "a page reopened at its newest card follows again");

        watch = new();
        Check(watch.Appeared("b") == new Scroll("b"), "coming back shows the newest card to a following reader");
        watch.Stop();
        Check(watch.Appeared("b") is null, "coming back moves nothing for a reader who was reading");
        // An empty pane follows its page's end.
        var empty = new MightyStructureFollow.PageWatch();
        Check(empty.Observe(At(0, 40, null, id: null), false) is null && empty.Following, "an empty pane follows its page's end");
        return Task.CompletedTask;
    }

    internal static Task AUsersMoveIsJudgedWhenItHappens()
    {
        static Page At(double origin, double document, double newest, double visible = 600) => new(origin, visible, document, "b", newest);
        // Symptom (Mac CI, trackpad smoke): the page moved 180pt up from the newest card, yet kept following. Its report arrived late, together with a layout change, and was judged as layout.
        var watch = new MightyStructureFollow.PageWatch();
        watch.Observe(At(1_523, 2_100, 2_091, 577), false);
        Check(watch.Following, "the page follows at the newest card");
        var blind = new MightyStructureFollow.PageWatch(); blind.Observe(At(1_523, 2_100, 2_091, 577), false);
        Check(blind.Observe(At(1_343, 2_130, 2_121, 577), false) == Scroll.Bottom && blind.Following, "a late report with no move known pulls the reader back");
        // The page tells the rule the moment it moves.
        watch.UserScrolled(-180);
        Check(!watch.Following && watch.Observe(At(1_343, 2_130, 2_121, 577), false) is null && !watch.Following, "a user's move is judged when it happens, and the late report keeps the reader where they are");

        watch = new(); watch.Observe(At(1_400, 2_000, 1_992), false);
        // 20pt up: the newest card's bottom is hidden by less than the slack. A sideways move keeps the state it had.
        watch.UserScrolled(-20);
        Check(watch.Following, "a move near the newest card keeps following");
        watch.UserScrolled(0);
        Check(watch.Following, "a sideways move keeps the state");
        // Its report is judged as the scroll it is: still at the newest card, nothing moves. A growth after it is followed.
        Check(watch.Observe(At(1_380, 2_000, 1_992), false) is null && watch.Observe(At(1_380, 2_300, 2_292), false) == Scroll.Bottom && watch.Following, "the move's report is a scroll, and a growth after it is followed");

        watch = new();
        watch.UserScrolled(-300);
        Check(watch.Following, "a move before any report judges nothing");
        watch.Observe(At(1_400, 2_000, 1_992), false);
        watch.UserScrolled(-300);
        Check(!watch.Following, "a move away stops following");
        watch.UserScrolled(300);
        Check(watch.Following, "a move back to the newest card follows again");
        return Task.CompletedTask;
    }
}
