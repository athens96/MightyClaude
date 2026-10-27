using System.Text.Json;
using MightyClaude.Core;

namespace MightyClaude.WinUI;

// The Mighty views of the web page (native/windows/web/src/mighty) on the window's side. The page lays the diagram out itself
// (its port of MightyGraphLayout) from the pane's graph runs it already holds; what needs this side comes from the pane's
// never-shown WinUI view (MainWindow.WebShell.Composer.cs) through the same Core calls the WinUI diagram makes: each block's
// copy (MightyGraphBlockModel.Blocks), the layout's inputs (the waiting plan, answered plans, pictures, background executions,
// the open result-file list, the history read from the session record), the timeline's groups and the structure's boards.
// Only while the page shows a pane's Mighty view (`mightyVisible`), at most once per 120 ms, and only what changed: blocks by id.

/// <summary>
/// One diagram block's copy (MightyGraphBlockModel.Blocks), without its frame (the page lays out) or its transcript (the page holds the runs).
/// A background sub-agent's or a task's block says so (<c>Background</c>); a task's block carries its run's last output lines
/// (<c>OutputTail</c>, from the host's memory, at most <see cref="BackgroundOutputTail.MaxLines"/>) and Claude's reply to its notice
/// (<c>NoticeReply</c>). Its output file never reaches the page's copy. Each is absent otherwise.
/// </summary>
internal sealed record WebMightyBlock(string Kind, string Title, string State, string? Capsule, string Help, string Indicator, string Outline, string Status, string Tone,
    string? FilesRunId, string? RecordId, bool FromRecord, string? Provider, bool? Background = null, IReadOnlyList<string>? OutputTail = null, string? NoticeReply = null);

internal sealed record WebMightyFile(string Path, int? Line);
internal sealed record WebMightyExecution(string Status, string StatusWord, string? Goal, string? Progress, IReadOnlyList<string> Counts, string? Line, string? Note, string Footer, string? Dashboard,
    string Key, bool ListOpen, string? ListNote, IReadOnlyList<WebMightyAC>? Acs, bool Opening, bool OpenFailed, double? Done);
/// <summary>One acceptance criterion of an opened execution card: its title, its state (pending, executing, completed, failed), the word for it and its level.</summary>
internal sealed record WebMightyAC(string Title, string Status, string StatusWord, int Depth);
internal sealed record WebMightyHistory(string Text, bool Acts, string Phase);

/// <summary>What the page's layout needs besides the runs it holds, and the toolbar's words.</summary>
internal sealed record WebMightyInputs(string? PlanRunID, IReadOnlyList<object> PlanRecords, IReadOnlyList<object> Galleries, IReadOnlyList<object> Executions,
    string? ResultFilesRunID, Dictionary<string, List<WebMightyFile>> Files, int Older, int RetainedStart, WebMightyHistory? History, string Summary, string SummaryHelp,
    Dictionary<string, WebMightyExecution> ExecutionCards, bool Animations, string Header);

internal sealed record WebTimelineRow(string NodeId, string Kind, string KindLabel, string Title, string Status, string StatusWord, string Tone, bool Ring, string? Rail,
    string? Latest, string? Duration, string? Capsule, string? Input, int? AgentIndex);
internal sealed record WebTimelineGroup(string RunId, int Ordinal, string Title, string Status, string StatusWord, string Tone, string Input, string Tally, bool Open, bool Older,
    IReadOnlyList<WebTimelineRow> Rows, string? ResultNodeId, string? ResultTone, string? Result, bool ResultFolds, string? ResultCaption);

internal sealed record WebStructureActivity(string Kind, string State, string Text);
/// <summary>A structure card for the page; an agent card adds its kind, "kind · model", its depth and its prompt apart from its output; <c>RevealId</c> is the block its reveal shows.
/// <c>Background</c>, <c>OutputTail</c> and <c>NoticeReply</c> are an agent card's as on <see cref="WebMightyBlock"/>.</summary>
internal sealed record WebStructureCard(string Id, string Kind, string RunId, string NodeId, string Title, string Status, string StatusWord, string Tone, string? Parent,
    string? Text, string? Message, string? Answer, IReadOnlyList<WebStructureActivity> Activity, string? Tally, bool Finished, string? ResultLine,
    string? NodeKind = null, string? Meta = null, int Depth = 0, string? Prompt = null, string? ExecutionNodeId = null, string? ResultNodeId = null, string? Tint = null,
    bool? Background = null, IReadOnlyList<string>? OutputTail = null, string? NoticeReply = null);
internal sealed record WebStructureColumn(string Id, string Title, string Step, string StepWord, int Count, IReadOnlyList<WebStructureCard> Cards, string? Tone);
internal sealed record WebStructureBoard(string Id, string Kind, int Ordinal, string? Headline, string Status, string StatusWord, string Tone, IReadOnlyList<string> RunIds, IReadOnlyList<WebStructureColumn> Columns, string? StyleName);

public sealed partial class MainWindow
{
    /// <summary>The panes whose Mighty view the page shows, and what was last pushed for each.</summary>
    private sealed class WebMightySent
    {
        internal RunSession? Session;
        internal readonly Dictionary<string, string> Blocks = new(StringComparer.Ordinal);
        internal string Inputs = "", Older = "", Timeline = "", Structure = "";
        internal bool Dirty = true;
    }
    private readonly Dictionary<string, WebMightySent> webMighty = new(StringComparer.Ordinal);
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? webMightyTimer;
    /// <summary>Blocks set in the last push, for the smoke: a streamed change must send only the block it changed.</summary>
    internal List<string> WebMightyLastSet { get; } = [];

    private void RegisterMightyMethods(WebBridge bridge)
    {
        // The page shows (or stops showing) a pane's Mighty view: pushes start with the whole model and stop when it hides.
        bridge.Register("mightyVisible", args =>
        {
            var paneId = args.Text("paneId") ?? "";
            if (Flag(args, "visible"))
            {
                var view = WebPane(paneId);
                webMighty[paneId] = new WebMightySent();
                view.WebMightyShown(true);
                StartWebMighty();
            }
            else if (webMighty.Remove(paneId) && views.TryGetValue(paneId, out var view)) view.WebMightyShown(false);
            return Task.FromResult<object?>(null);
        });
        bridge.Register("mightySetMode", async args => { await WebPane(args.Text("paneId")).SetAgentViewMode(args.Text("mode") == "mighty" ? "mighty" : "default"); return null; });
        bridge.Register("mightySetPresentation", async args => { await WebPane(args.Text("paneId")).SetGraphPresentation(args.Text("mode") ?? "diagram"); MightyDirty(args.Text("paneId")); return null; });
        bridge.Register("mightySaveSize", async args =>
        {
            var size = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("width", out var w) && w.TryGetDouble(out var width) && args.TryGetProperty("height", out var h) && h.TryGetDouble(out var height)
                ? new GraphBlockSize(width, height) : null;
            await WebPane(args.Text("paneId")).WebSaveSize(args.Text("nodeId") ?? "", args.Text("target") ?? "block", size);
            return null;
        });
        bridge.Register("mightyExpand", async args => { await WebPane(args.Text("paneId")).WebExpand(args.Text("nodeId") ?? "", Flag(args, "expanded")); MightyDirty(args.Text("paneId")); return null; });
        bridge.Register("mightyResultFiles", args => { WebPane(args.Text("paneId")).WebResultFiles(args.Text("runId")); MightyDirty(args.Text("paneId")); return Task.FromResult<object?>(null); });
        bridge.Register("mightyLoadOlder", args => { WebPane(args.Text("paneId")).LoadOlderGraphHistory(); MightyDirty(args.Text("paneId")); return Task.FromResult<object?>(null); });
        bridge.Register("mightyImages", async args => await WebPane(args.Text("paneId")).WebGalleryImages(args.Text("runId") ?? "", args.Text("step") ?? "", Flag(args, "expanded")));
        bridge.Register("mightyOpenDashboard", async args =>
        {
            var paneId = args.Text("paneId"); var opening = WebPane(paneId).WebOpenDashboard(args.Text("key") ?? "");
            MightyDirty(paneId); await opening; MightyDirty(paneId);
            return null;
        });
        bridge.Register("mightyTimelineFlip", args => { WebPane(args.Text("paneId")).WebTimelineFlip(args.Text("runId") ?? ""); MightyDirty(args.Text("paneId")); return Task.FromResult<object?>(null); });
    }

    private void MightyDirty(string? paneId) { if (paneId is not null && webMighty.TryGetValue(paneId, out var sent)) sent.Dirty = true; }

    private void StartWebMighty()
    {
        if (webMightyTimer is null)
        {
            // One draw per 120 ms at most (the WinUI diagram's QueueGraphRefresh gap): a streamed run asks for one per event.
            webMightyTimer = DispatcherQueue.CreateTimer(); webMightyTimer.Interval = TimeSpan.FromMilliseconds(120); webMightyTimer.IsRepeating = true;
            webMightyTimer.Tick += (_, _) => FlushWebMighty();
        }
        if (!webMightyTimer.IsRunning) webMightyTimer.Start();
        FlushWebMighty();
    }

    private long webMightyTicks;

    /// <summary>
    /// Each shown pane whose record changed (or whose inputs may have: a history read, a file lookup, a dashboard read, every
    /// second) is projected again; only blocks whose copy changed, and the inputs, timeline or boards when they changed, go out.
    /// </summary>
    private void FlushWebMighty()
    {
        if (webShell is null || webMighty.Count == 0) { webMightyTimer?.Stop(); return; }
        var slow = ++webMightyTicks % 8 == 0;
        foreach (var (paneId, sent) in webMighty.ToArray())
        {
            var session = service.Snapshot.Sessions.FirstOrDefault(s => s.Id == paneId);
            if (session is null || !views.TryGetValue(paneId, out var view)) { webMighty.Remove(paneId); continue; }
            if (!sent.Dirty && !slow && ReferenceEquals(session, sent.Session)) continue;
            sent.Dirty = false; sent.Session = session;
            using var perf = PerfLog.Measure("web mighty");
            var model = view.WebMightyModel(session);
            var set = new Dictionary<string, WebMightyBlock>(StringComparer.Ordinal);
            foreach (var (id, block) in model.Blocks)
            {
                var json = JsonSerializer.Serialize(block, Wire.Json);
                if (sent.Blocks.TryGetValue(id, out var last) && last == json) continue;
                sent.Blocks[id] = json; set[id] = block;
            }
            var removed = sent.Blocks.Keys.Where(id => !model.Blocks.ContainsKey(id)).ToList();
            foreach (var id in removed) sent.Blocks.Remove(id);
            var inputs = JsonSerializer.Serialize(model.Inputs, Wire.Json);
            var older = model.OlderKey;
            var timeline = model.Timeline is null ? sent.Timeline : JsonSerializer.Serialize(model.Timeline, Wire.Json);
            var structure = model.Structure is null ? sent.Structure : JsonSerializer.Serialize(model.Structure, Wire.Json);
            var message = new Dictionary<string, object?> { ["kind"] = "mighty", ["paneId"] = paneId };
            if (set.Count > 0) message["blocks"] = set;
            if (removed.Count > 0) message["removed"] = removed;
            if (inputs != sent.Inputs) { message["inputs"] = model.Inputs; sent.Inputs = inputs; }
            if (older != sent.Older) { message["older"] = model.OlderRuns.Select(WebDelta.PageRun).ToList(); sent.Older = older; }
            if (timeline != sent.Timeline) { message["timeline"] = model.Timeline; sent.Timeline = timeline; }
            if (structure != sent.Structure) { message["structure"] = model.Structure; sent.Structure = structure; }
            WebMightyLastSet.Clear(); WebMightyLastSet.AddRange(set.Keys);
            if (message.Count == 2) continue;
            webShell.Bridge.Push(message);
        }
    }

    /// <summary>
    /// The Mighty part of the web smoke: the fixture pane gets three finished requests and one running (no CLI, no AI request)
    /// and is put in Mighty mode. The page must draw every block, the edges (one into a running block) and the activity
    /// outline; a streamed change of one sub-agent's entry must draw that block again and no other (each block counts its draws
    /// in data-renders) with a small delta; the log and the pipeline views must draw the requests.
    /// </summary>
    private async Task<bool> RunWebMightySmoke(Microsoft.Web.WebView2.Core.CoreWebView2 core, string paneId, Dictionary<string, object?> result)
    {
        var shell = webShell ?? throw new InvalidOperationException("web shell missing");
        async Task<bool> Wait(string script, int seconds = 15)
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < deadline) { if (await core.ExecuteScriptAsync(script) == "true") return true; await Task.Delay(100); }
            return false;
        }
        async Task<string> Read(string script) => JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync(script)) ?? "";
        // One fixed time: the streamed copy of the running request must equal the first in all but the changed word.
        var at = Wire.Now();
        LogEntry Line(string id, string text) => new(id, "assistant", text, at, "claude");
        MightyGraphRun Finished(int n) => new()
        {
            Id = "smoke-run-" + n, Input = "fixture request " + n, Status = "completed", Provider = "claude", FinalOutput = "fixture answer " + n,
            RootEntries = [Line($"smoke-{n}-root", "working " + n)], ResultEntries = [Line($"smoke-{n}-result", "fixture answer " + n)],
            Agents = [new() { Id = "reader", Title = "Reader", Input = "read " + n, Status = "completed", Entries = [Line($"smoke-{n}-reader", "read " + n)] }],
        };
        MightyGraphRun Running(string writer) => new()
        {
            Id = "smoke-run-4", Input = "fixture request 4", Status = "running", Provider = "claude", RootEntries = [Line("smoke-4-root", "planning")],
            Agents =
            [
                new() { Id = "reader", Title = "Reader", Input = "read 4", Status = "completed", Entries = [Line("smoke-4-reader", "read 4")] },
                new() { Id = "writer", Title = "Writer", Input = "write 4", Status = "running", Entries = [Line("smoke-4-writer", writer)] },
            ],
        };
        var writerId = MightyGraphBlockSize.NodeId("smoke-run-4", "agent:writer");
        try
        {
            await service.UpdateAsync(state => state with { Sessions = state.Sessions.Select(s => s.Id != paneId ? s : s with
            {
                AgentViewMode = "mighty", GraphViewMode = "diagram", GraphRuns = [Finished(1), Finished(2), Finished(3), Running("writing")],
            }).ToList() });
            // 3 × (request, 실행, sub-agent, result) and the running request's request, 실행 and two sub-agents.
            const string Blocks = "document.querySelectorAll('[data-testid=mighty-diagram] .mighty-block').length";
            var drawn = await Wait(Blocks + " >= 16 && !!document.querySelector('.mighty-edges-quiet')?.getAttribute('d') && !!document.querySelector('.mighty-edges-live')?.getAttribute('d') && !!document.querySelector('[data-outline]')");
            var counts = await Read("JSON.stringify({ blocks: " + Blocks + ", edges: document.querySelector('[data-testid=mighty-diagram]')?.dataset.edgeCount, outline: document.querySelector('[data-outline]')?.dataset.outline ?? null, titled: [...document.querySelectorAll('.mighty-title')].filter(e => e.textContent.length > 0).length })");
            result["mightyDiagram"] = new { drawn, counts };
            // Every block's draw count before the stream, then one word more in the writer's entry, as a streamed graph_run.
            const string Renders = "JSON.stringify(Object.fromEntries([...document.querySelectorAll('.mighty-block')].map(e => [e.dataset.blockId, e.dataset.renders])))";
            // The diagram settles first (the newest result's height is measured after its first draw and moves what is under it):
            // the counts are taken once two reads 700 ms apart agree, so only what the stream itself draws is counted.
            var settledAt = DateTime.UtcNow.AddSeconds(15); var settling = await Read(Renders);
            // Settled once the counts held for 1.5 s (three reads 500 ms apart): a windowed block transcript can still be measuring after one quiet read.
            for (var quiet = 0; quiet < 3 && DateTime.UtcNow < settledAt;) { await Task.Delay(500); var again = await Read(Renders); quiet = again == settling ? quiet + 1 : 0; settling = again; }
            var before = JsonSerializer.Deserialize<Dictionary<string, string>>(settling) ?? [];
            await service.UpdateAsync(state => state with { Sessions = state.Sessions.Select(s => s.Id != paneId ? s : s with
            {
                GraphRuns = [.. (s.GraphRuns ?? []).Take(3), Running("writing streamed-marker")],
            }).ToList() });
            var streamed = await Wait($"(() => {{ const e = document.querySelector('[data-block-id=\"{writerId}\"]'); return !!e && Number(e.dataset.renders) > {(before.TryGetValue(writerId, out var w) ? w : "0")}; }})()");
            await Task.Delay(500);
            var after = JsonSerializer.Deserialize<Dictionary<string, string>>(await Read(Renders)) ?? [];
            var redrawn = after.Where(pair => !before.TryGetValue(pair.Key, out var was) || was != pair.Value).Select(pair => pair.Key).ToList();
            var deltaBytes = shell.Bridge.LastDeltaBytes;
            var why = await Read("JSON.stringify(Object.fromEntries([...document.querySelectorAll('.mighty-block')].filter(e => e.dataset.changed).map(e => [e.dataset.blockId, e.dataset.changed])))");
            result["mightyStream"] = new { streamed, redrawn, deltaBytes, blocksPushed = WebMightyLastSet.ToList(), why };
            var onlyChanged = streamed && redrawn.Count == 1 && redrawn[0] == writerId && deltaBytes < 16 * 1024;
            await core.ExecuteScriptAsync("document.querySelector('button[data-presentation=timeline]')?.click()");
            var timeline = await Wait("document.querySelectorAll('[data-testid=timeline-group]').length === 4");
            await core.ExecuteScriptAsync("document.querySelector('button[data-presentation=structure]')?.click()");
            var structure = await Wait("document.querySelectorAll('[data-testid=mighty-structure] [data-column-id]').length >= 3");
            // A card's reveal: the diagram shows again with that block selected and the camera on it.
            var revealClicked = structure && await core.ExecuteScriptAsync("(() => { const b = document.querySelector('[data-testid=mighty-structure] [data-card-id] [data-testid=structure-reveal]'); b?.click(); return !!b; })()") == "true";
            var revealed = revealClicked && await Wait("(() => { if (document.querySelector('[data-testid=mighty]')?.dataset.presentation !== 'diagram') return false; const v = document.querySelector('[data-testid=mighty-diagram] .mighty-viewport')?.getBoundingClientRect(); const b = document.querySelector('[data-testid=mighty-diagram] .mighty-block.selected')?.getBoundingClientRect(); "
                + "return !!v && !!b && b.bottom > v.top && b.top < v.bottom && b.right > v.left && b.left < v.right; })()");
            result["mightyViews"] = new { timeline, structure, revealClicked, revealed, mode = service.Snapshot.Sessions.FirstOrDefault(s => s.Id == paneId)?.GraphViewMode };
            return drawn && onlyChanged && timeline && structure && revealed;
        }
        finally
        {
            await service.UpdateAsync(state => state with { Sessions = state.Sessions.Select(s => s.Id != paneId ? s : s with { AgentViewMode = "default", GraphViewMode = null, GraphRuns = null }).ToList() });
        }
    }

    internal sealed record WebMightyProjection(Dictionary<string, WebMightyBlock> Blocks, WebMightyInputs Inputs, string OlderKey, IReadOnlyList<MightyGraphRun> OlderRuns,
        IReadOnlyList<WebTimelineGroup>? Timeline, IReadOnlyList<WebStructureBoard>? Structure);

    private sealed partial class PaneView
    {
        private bool webMightyShown;
        private readonly HashSet<string> webTimelineFlipped = new(StringComparer.Ordinal);

        /// <summary>The page shows this pane's Mighty view: the dashboard reads of its executions run only then (as <see cref="OuroborosShown"/> for WinUI).</summary>
        internal void WebMightyShown(bool shown)
        {
            webMightyShown = shown;
            if (!shown) StopOuroboros();
        }

        /// <summary>The pane's Mighty view as the page draws it, read with the same Core calls <see cref="DrawGraphCore"/> makes.</summary>
        internal WebMightyProjection WebMightyModel(RunSession pane)
        {
            var retained = (IReadOnlyList<MightyGraphRun>)(pane.GraphRuns ?? []);
            var older = GraphHistoryRuns(pane, retained);
            var runs = (IReadOnlyList<MightyGraphRun>)[.. older, .. retained];
            var presentation = MightyTimeline.Mode(pane);
            var files = ResolveResultFiles(retained);
            graphResultFiles.Publish(retained.Where(run => files.ContainsKey(run.Id)).Select(run => (run.Id, files[run.Id].Count > 0)).ToList());
            var planRunId = PlanCardSupport.DiagramPlanRunID(PendingPlan, true, runs);
            var executions = graphExecutionLinks = OuroborosExecutionLinks.Extract(retained);
            if (presentation == "diagram") WatchOuroboros(); else StopOuroboros();
            var root = LocalPath(Workspace);
            var galleries = MightyGraphImages.Galleries(runs, root, older.Count);
            var records = PlanCardSupport.DiagramRecords(pane.PlanHistory, runs);
            // The copy is the layout's blocks; frames do not matter here, so the layout is made without a viewport.
            var layout = MightyGraphLayout.Make(runs, pane.Draft, pane.Status == "running", graphExpanded, graphResultFilesRunId, splitsExecution: true,
                planRunID: planRunId, planRecords: records, galleries: galleries, executions: executions.Select(link => (link.RunID, link.Key)).ToList(),
                retainedStart: older.Count, history: ShowsHistoryBlock(pane, retained));
            graphPlanNodeId = planRunId is null ? null : MightyGraphBlockSize.NodeId(planRunId, MightyGraphLayout.PlanSuffix);
            graphLatestResultId = MightyGraphLayout.LatestResultID(runs);
            var catalog = PaneRuntime(pane.Provider)?.ModelCatalog?.Models;
            var blocks = MightyGraphBlockModel.Blocks(layout, runs, pane.Draft, ProviderMark.Label(pane.Provider), AnimationsEnabled, catalog);
            // The background sub-agents and tasks by their diagram node, for their kind, output lines and notice reply.
            var tails = owner.BackgroundOutputFor(pane.Id);
            var background = new Dictionary<string, MightyGraphAgent>(StringComparer.Ordinal);
            foreach (var run in runs)
                foreach (var agent in run.Agents)
                    if (agent.IsTask || agent.IsBackgroundAgent) background.TryAdd(MightyGraphLayout.NodeID(run, "agent:" + agent.Id), agent);
            var copy = new Dictionary<string, WebMightyBlock>(StringComparer.Ordinal);
            var cards = new Dictionary<string, WebMightyExecution>(StringComparer.Ordinal);
            foreach (var raw in blocks)
            {
                var block = raw;
                var index = -1;
                for (var i = 0; i < runs.Count; i++) if (block.Kind is "request" && block.Id == MightyGraphLayout.NodeID(runs[i], "request")) { index = i; break; }
                if (index >= 0) block = block with { Title = GraphRequestTitle(runs[index], index + 1) };
                if (block.Kind == MightyGraphLayout.ExecutionKind)
                {
                    block = ExecutionBlock(block);
                    if (block.RecordId is { } key && executions.FirstOrDefault(link => link.Key == key) is { } link) cards[block.Id] = WebExecutionCard(key, link, graphExpanded.Contains(block.Id));
                }
                var fromRecord = LoadedFromRecord(block.Id, older) && block.Kind is "request" or MightyGraphLayout.WorkKind;
                var held = background.GetValueOrDefault(block.Id);
                copy[block.Id] = new(block.Kind, block.Title, block.State, block.Capsule, block.CapsuleHelp, block.Indicator, block.Outline, block.Status,
                    MightyGraphBlockModel.Tone(block).ToString().ToLowerInvariant(), block.ResultFilesRunId, block.RecordId, fromRecord, MightyGraphBlockModel.TitleProvider(block, pane.Provider),
                    held is null ? (bool?)null : true, held is { IsTask: true } ? MobileMightySupport.OutputTail(tails.GetValueOrDefault(held.Id)) : null,
                    held?.NoticeReply is { Length: > 0 } reply ? reply : null);
            }
            WebMightyHistory? history = null;
            if (ShowsHistoryBlock(pane, retained))
            {
                var state = graphHistory.Connects(retained.FirstOrDefault()?.Id, pane.ResumeId) ? graphHistory : new SessionHistoryState();
                history = new(state.BlockText(older.Count), state.BlockActs, state.Phase.ToString().ToLowerInvariant());
            }
            var summary = MightyGraphBlockModel.ToolbarSummary(retained)
                + (older.Count > 0 ? " · " + Locale.Get("graph.history.headerLoaded", new Dictionary<string, string> { ["count"] = older.Count.ToString() }) : "");
            var phase = activeStyle is null ? null : GuidedPhase(activeStyle);
            var header = Locale.Get("graph.view.mighty") + (activeStyle is null ? "" : " · " + activeStyle.Manifest.Name + (phase is null ? "" : " · " + phase.Title));
            var inputs = new WebMightyInputs(planRunId,
                records.Select(r => (object)new { runID = r.RunID, recordID = r.RecordID }).ToList(),
                galleries.Select(g => (object)new { runID = g.RunID, step = g.Step, count = g.Count }).ToList(),
                executions.Select(e => (object)new { runID = e.RunID, key = e.Key }).ToList(),
                graphResultFilesRunId, files.ToDictionary(f => f.Key, f => f.Value.Select(file => new WebMightyFile(file.Path, file.Line)).ToList()),
                older.Count, older.Count, history, summary, MightyGraphBlockModel.ToolbarHelp(retained), cards, AnimationsEnabled, header);
            var olderKey = older.Count + ":" + string.Join(",", older.Select(run => run.Id + "/" + run.Status + "/" + run.Agents.Count));
            return new(copy, inputs, olderKey, older,
                presentation == "timeline" ? WebTimeline(pane, runs, older.Count, catalog) : null,
                presentation == "structure" ? WebStructure(pane, runs) : null);
        }

        /// <summary>A background execution's card (BuildOuroborosCard), its words built here.</summary>
        private WebMightyExecution WebExecutionCard(string key, OuroborosExecutionLink link, bool listOpen)
        {
            var snapshot = graphOuroboros?.Snapshot(key);
            var status = snapshot?.Status ?? OuroborosExecutionStatus.Unknown;
            var word = status.ToString().ToLowerInvariant();
            string? progress = null;
            if (snapshot?.Total is > 0 and var total)
                progress = Locale.Get("graph.ouroboros.progress", new Dictionary<string, string> { ["completed"] = Math.Min(total, snapshot.Completed ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture), ["total"] = total.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            var counts = new List<string>();
            if (snapshot is { Source: OuroborosExecutionSnapshot.Sources.Live })
                foreach (var (name, value) in new[] { ("pending", snapshot.Pending), ("executing", snapshot.Executing), ("completed", snapshot.Completed), ("failed", snapshot.Failed) })
                    counts.Add(Locale.Get("graph.ouroboros.count." + name, new Dictionary<string, string> { ["n"] = (value ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture) }));
            var line = string.Join(" · ", new[] { snapshot?.Phase, snapshot?.Activity }.OfType<string>());
            var note = snapshot?.Source switch
            {
                null => "graph.ouroboros.waiting", OuroborosExecutionSnapshot.Sources.Unreachable => "graph.ouroboros.unreachable",
                OuroborosExecutionSnapshot.Sources.Unreadable => "graph.ouroboros.unreadable", OuroborosExecutionSnapshot.Sources.NoExecution => "graph.ouroboros.noExecution", _ => null,
            };
            var footer = link.ExecutionID is { } execution ? Locale.Get("graph.ouroboros.executionID", new Dictionary<string, string> { ["id"] = execution })
                : Locale.Get("graph.ouroboros.jobID", new Dictionary<string, string> { ["id"] = link.JobID ?? "" });
            // The AC list while it is open (BuildOuroborosACs): the first 300, or a line saying it is being read or there is none.
            var items = snapshot?.Items;
            var acs = listOpen && items is { Count: > 0 } ? items.Take(300).Select(item =>
            {
                var state = item.Status.ToString().ToLowerInvariant();
                return new WebMightyAC(item.Title, state, Locale.Get("graph.ouroboros.ac." + state), item.Depth);
            }).ToList() : null;
            var listNote = listOpen && acs is null ? Locale.Get(items is null ? "graph.ouroboros.loadingACs" : "graph.ouroboros.noACs") : null;
            var failed = graphOuroboros?.OpenFailed(key) == true;
            double? done = snapshot?.Total is > 0 and var all ? Math.Min(all, snapshot.Completed ?? 0) / (double)all : null;
            return new(word, Locale.Get("graph.ouroboros.status." + word), snapshot?.Goal, progress, counts, line.Length > 0 ? line : null, note is null ? null : Locale.Get(note),
                failed ? Locale.Get("graph.ouroboros.openFailed") : footer, (snapshot?.DashboardURL ?? link.DashboardURL)?.AbsoluteUri,
                key, listOpen, listNote, acs, graphOuroboros?.Opening(key) == true, failed, done);
        }

        /// <summary>The card's "open dashboard" (<see cref="OpenOuroborosDashboard"/>): the run's page on the loopback dashboard, started first when nothing answers.</summary>
        internal async Task WebOpenDashboard(string key)
        {
            if (graphExecutionLinks.FirstOrDefault(link => link.Key == key) is { } link) await OpenOuroborosDashboard(link);
        }

        /// <summary>The timeline's groups (MightyTimeline.Groups), each row's words built here.</summary>
        private List<WebTimelineGroup> WebTimeline(RunSession pane, IReadOnlyList<MightyGraphRun> runs, int older, IReadOnlyList<ModelOption>? catalog)
        {
            static string Tone(DesignTone tone) => tone.ToString().ToLowerInvariant();
            var groups = new List<WebTimelineGroup>();
            foreach (var group in MightyTimeline.Groups(runs))
            {
                var run = runs[group.RunIndex];
                var rows = group.Rows.Select(row =>
                {
                    var agent = row.AgentIndex is { } at && at < run.Agents.Count ? run.Agents[at] : null;
                    var capsule = row.IsExecution ? ModelUsageFormat.BlockCapsule(run.Usage, run.ResponseRecords ?? [], run.NodeModelLabel, catalog, versioned: true)
                        : agent is not null ? ModelUsageFormat.BlockCapsule(agent.Usage, agent.ResponseRecords ?? [], null, catalog, versioned: true) : null;
                    return new WebTimelineRow(row.NodeId, row.Kind, MightyTimeline.KindLabel(row.Kind, agent?.Input), row.Title, row.Status,
                        row.Status == MightyTimeline.RequestStatus ? "" : MightyGraphBlockModel.StateLabel(row.Status), Tone(row.Node.Tone), row.Node.Ring,
                        row.Node.Rail is { } rail ? Tone(rail) : null, row.Latest, MightyTimeline.DurationLabel(row.DurationMs), capsule,
                        (agent is null ? null : MightyGraphSupport.ShownInput(agent)) ?? (row.Kind == "main" ? run.Input : null), row.AgentIndex);
                }).ToList();
                var tally = Locale.Get("phone.blocks.tally", new Dictionary<string, string> { ["total"] = group.Tally.Total.ToString(), ["settled"] = group.Tally.Settled.ToString() });
                var total = run.TotalUsage;
                var caption = Locale.Get("graph.timeline.requestOrdinal", new Dictionary<string, string> { ["n"] = group.Ordinal.ToString() }) + (total is null || total.IsEmpty ? "" : " · " + total.Summary);
                groups.Add(new(group.RunId, group.Ordinal, GraphRequestTitle(run, group.Ordinal), group.Status, MightyGraphBlockModel.RunStateLabel(run),
                    Tone(StatusGlyph.Tone(group.Status)), group.Input, tally, MightyTimeline.GroupOpen(group, runs.Count, webTimelineFlipped), group.RunIndex < older, rows,
                    group.Result?.NodeId, group.Result is { } r ? Tone(r.Tone) : null, group.Result?.Text, group.Result?.Text is { } text && MightyTimeline.Folds(text), group.Result is null ? null : caption));
            }
            return groups;
        }

        /// <summary>The structure's boards (MightyStructure.Make, Boards), each card's words built here.</summary>
        private List<WebStructureBoard> WebStructure(RunSession pane, IReadOnlyList<MightyGraphRun> runs)
        {
            static string Tone(string status) => StatusGlyph.Tone(MightyTimeline.Status(status)).ToString().ToLowerInvariant();
            // The 파이프라인 view's state colours (Core MightyStructure.Tone), null for a step still to come.
            static string? ToneWord(DesignTone? tone) => tone?.ToString().ToLowerInvariant();
            var (style, phase, pending) = StructureInputs(runs, styleStartingNew);
            var tails = owner.BackgroundOutputFor(pane.Id);
            var signature = new MightyStructure.Signature(runs, style, phase, styleStartingNew, pane.PlanHistory, pending);
            var boards = MightyStructure.Boards(MightyStructure.Make(runs, style, phase, styleStartingNew, pane.PlanHistory, pending, structureItems.Items(runs, signature)));
            string StepWord(MightyStructure.Step step) => Locale.Get("graph.structure.step." + step switch
            {
                MightyStructure.Step.Done => "done", MightyStructure.Step.Current => "current", MightyStructure.Step.Error => "done", MightyStructure.Step.Stopped => "done", _ => "pending",
            });
            WebStructureCard Card(MightyStructure.Card card, MightyStructure.BoardColumn column)
            {
                var item = card.Item;
                var title = card.Node?.Title ?? GraphRequestTitle(runs[item.RunIndex], item.Ordinal);
                var status = card.Node?.Status ?? item.Status;
                var activity = (card.Node?.Activity ?? item.Activity).Select(a => new WebStructureActivity(a.Kind, a.State, a.Text)).ToList();
                var node = card.Node;
                var model = node?.Model is { } id ? ModelUsageFormat.ShortName(id, null, versioned: true) : null;
                var text = card.Kind switch
                {
                    MightyStructure.CardKind.Agent => card.Node?.Output?.Preview,
                    MightyStructure.CardKind.Request => item.Request?.Preview,
                    _ => item.Request?.Preview,
                };
                return new(card.Id, card.Kind.ToString().ToLowerInvariant(), item.RunId, card.Node?.NodeId ?? (card.Kind == MightyStructure.CardKind.Execution ? item.ExecutionNodeId : item.NodeId),
                    title, status, MightyGraphBlockModel.StateLabel(status), Tone(status), card.Parent is null ? null : Locale.Get("graph.structure.underParent", new Dictionary<string, string> { ["title"] = card.Parent }),
                    text, item.Message?.Preview, item.Answer?.Text, activity,
                    Locale.Get("graph.structure.tally", new Dictionary<string, string> { ["agents"] = item.Tally.Agents.ToString(), ["tools"] = item.Tally.Tools.ToString() }),
                    item.Finished, MightyStructure.Fold.ResultLine(item),
                    // A task notice's title already names its kind (MightyStructure.Kept keeps no other steer).
                    node?.Kind, node is null ? null : string.Join(" · ", new[] { node.Kind == "steer" ? null : MightyTimeline.KindLabel(node.Kind, null), model }.Where(p => !string.IsNullOrEmpty(p))),
                    node?.Depth ?? 0, node?.Prompt?.Preview, item.ExecutionNodeId, MightyGraphLayout.NodeID(runs[item.RunIndex], "result"), ToneWord(MightyStructure.Tone.OfCard(card, column)),
                    node is { Background: true } ? true : (bool?)null, node is { Kind: "task" } ? MobileMightySupport.OutputTail(tails.GetValueOrDefault(node.AgentId)) : null, node?.NoticeReply?.Text);
            }
            var styleName = activeStyle?.Manifest.Name;
            return boards.Select(board => new WebStructureBoard(board.Id, board.Kind.ToString().ToLowerInvariant(), board.Ordinal, board.Headline, board.Status, MightyGraphBlockModel.StateLabel(board.Status), Tone(board.Status), board.RunIds,
                board.Columns.Select(column => new WebStructureColumn(column.Id, column.Step.Title ?? Locale.Get("graph.structure.column." + column.Id), column.Step.Step.ToString().ToLowerInvariant(),
                    StepWord(column.Step.Step), column.Count, column.Cards.Select(card => Card(card, column)).ToList(), ToneWord(MightyStructure.Tone.OfColumn(column)))).ToList(), board.Kind == MightyStructure.BoardKind.Pipeline ? styleName : null)).ToList();
        }

        /// <summary>A block drag released in the page, saved as the WinUI drag saves it (<see cref="EndResultResize"/>): the newest result and the waiting plan keep the pane's one size, any other block its own.</summary>
        internal Task WebSaveSize(string nodeId, string target, GraphBlockSize? size) => owner.Act(async () =>
        {
            switch (target)
            {
                case "result": await Change(p => p with { GraphResultSize = size?.Normalized }); break;
                case "plan": await Change(p => p with { GraphPlanSize = size?.Normalized }); break;
                default:
                    if (size is null) { graphRecordSizes.Remove(nodeId); await Change(p => GraphBlockPreferences.Set(p, nodeId, null)); }
                    else if (!LoadedFromRecord(nodeId, graphHistory.Runs)) await Change(p => GraphBlockPreferences.Set(p, nodeId, size));
                    else if (size.NormalizedFor(nodeId) is { } kept) graphRecordSizes[nodeId] = kept;
                    break;
            }
        });

        /// <summary>The expand / collapse control (<see cref="AddGraphBlockSizeControls"/>): the saved size goes, the block opens or folds.</summary>
        internal Task WebExpand(string nodeId, bool expanded) => owner.Act(async () =>
        {
            graphRecordSizes.Remove(nodeId);
            await Change(p => GraphBlockPreferences.Set(p, nodeId, null));
            if (expanded) graphExpanded.Add(nodeId); else graphExpanded.Remove(nodeId);
        });

        /// <summary>A result card's file toggle (<see cref="ToggleResultFiles"/>), or the list's close with no run.</summary>
        internal void WebResultFiles(string? runId)
        {
            if (runId is null) graphResultFiles.Close();
            else graphResultFiles.Toggle(runId, graphResultFilesByRun.TryGetValue(runId, out var named) && named.Files.Count > 0);
        }

        internal void WebTimelineFlip(string runId) { if (!webTimelineFlipped.Remove(runId)) webTimelineFlipped.Add(runId); }

        /// <summary>A step's pictures (MightyGraphImages.Items) in a run kept in the pane or its record; null for a run neither holds.</summary>
        private List<AgentImageItem>? WebGalleryItems(string runId, string step)
        {
            var pane = Session;
            var retained = (IReadOnlyList<MightyGraphRun>)(pane.GraphRuns ?? []);
            var runs = (IReadOnlyList<MightyGraphRun>)[.. GraphHistoryRuns(pane, retained), .. retained];
            return runs.FirstOrDefault(r => r.Id == runId) is { } run ? MightyGraphImages.Items(run, step, LocalPath(Workspace)) : null;
        }

        /// <summary>
        /// A step's pictures as the page's tiles show them: the visible ones as data URLs (the page's policy allows them; an SVG
        /// drawn to PNG, <see cref="WebImageUrl"/>), the rest counted.
        /// </summary>
        internal async Task<object?> WebGalleryImages(string runId, string step, bool expanded)
        {
            if (WebGalleryItems(runId, step) is not { } items) return null;
            var shown = items.Take(MightyGraphLayout.VisibleImages(items.Count, expanded)).ToList();
            var tiles = new List<object>();
            foreach (var item in shown)
            {
                var data = await Task.Run(() => item.Stored is { } stored ? owner.service.Images.Data(stored) : item.Located switch
                {
                    AgentImageLocation.File file => AgentImagePaths.Read(file.Path, file.Root),
                    AgentImageLocation.Inline inline => AgentImageSupport.DecodeBase64(inline.Base64),
                    _ => null,
                });
                var type = item.Stored?.MediaType ?? item.Located switch { AgentImageLocation.Inline inline => inline.MediaType, AgentImageLocation.File file => AgentImageSupport.MediaTypeForFileName(file.Path), _ => null } ?? "image/png";
                tiles.Add(new { id = item.Id, alt = item.Alt, url = await WebImageUrl(data, type) });
            }
            return new { count = items.Count, tiles };
        }

        /// <summary>The picture viewer's view of a diagram tile (MainWindow.GraphImages.cs BuildImageTile's Open): its item named by id, or null.</summary>
        internal TranscriptAction? WebGalleryImage(string runId, string step, string id) =>
            WebGalleryItems(runId, step)?.FirstOrDefault(i => i.Id == id) is { } item ? new("image", item.Source, item.Stored) : null;
    }
}
