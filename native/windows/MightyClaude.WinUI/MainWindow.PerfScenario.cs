using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using MightyClaude.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;

namespace MightyClaude.WinUI;

/// <summary>
/// The timing scenario (<c>--smoke-test --perf-scenario</c>, scripts/perf-scenario.ps1): the smoke's isolated profile and fixture
/// providers, a few long conversations (copied from <c>MIGHTY_PERF_STATE</c>'s workspace-state.json, or made up), and the
/// operations people find slow, each timed until the UI has drawn the result, while a 16 ms timer records how late the UI thread
/// answers. The result is perf-result.json in the profile. Nothing is sent to an AI and no CLI is started.
/// The phases drive the page: each operation is a DOM click or typed text through ExecuteScriptAsync, timed in the page with performance.now()
/// from the action until the animation frame after the DOM shows its result (<see cref="PerfPageHelper"/>), and each phase adds
/// the page's long tasks (PerformanceObserver 'longtask') as <c>pageLongTasks</c>. The stall probe and the GC figures stay the host's.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>How late the UI thread ran a 16 ms timer during one phase: a late tick is time nothing else could run either.</summary>
    private sealed class PerfStalls
    {
        internal int Ticks, Over100, Over250; internal double BeyondSum, Max;
        internal void Add(double late)
        {
            Ticks++; Max = Math.Max(Max, late);
            if (late > 33) BeyondSum += late - 33;
            if (late > 100) Over100++;
            if (late > 250) Over250++;
        }
        internal object Json() => new { ticks = Ticks, lateBeyond33SumMs = Math.Round(BeyondSum, 1), maxLateMs = Math.Round(Max, 1), over100 = Over100, over250 = Over250 };
    }

    /// <summary>The longest an operation is waited for; one that reaches it is listed in the phase's <c>capped</c> count, not taken as its time.</summary>
    private const int PerfSettleCapMs = 30000;
    private const int PerfIdleMs = 2000, PerfRounds = 20, PerfToggles = 10, PerfAddedPanes = 5, PerfStreamSeconds = 8, PerfEventsPerSecond = 25, PerfTypingEveryMs = 500, PerfSends = 10;

    /// <summary>
    /// The page's half of the web scenario, installed once: <c>__perf.start(body)</c> runs an operation's async body with its start
    /// time and, once the body's <c>until</c> sees the DOM show the result, waits for the next animation frame and leaves the
    /// elapsed milliseconds in <c>window.__perfOp</c>; a body that throws, or waits past the cap, leaves its error there too.
    /// </summary>
    private const string PerfPageHelper = "(() => { if (window.__perf) return true; const p = window.__perf = { longTasks: [], observing: false, frame: () => new Promise(r => requestAnimationFrame(() => r())), "
        + "slot: () => document.querySelector('.pane-slot.active:not(.hidden)'), "
        + "async until(cond, t0) { while (!cond()) { if (performance.now() - t0 > 29900) throw new Error('capped'); await p.frame(); } }, "
        + "start(body) { window.__perfOp = null; const t0 = performance.now(); (async () => { try { await body(t0); await p.frame(); window.__perfOp = { ms: performance.now() - t0 }; } "
        + "catch (e) { window.__perfOp = { ms: performance.now() - t0, error: String((e && e.message) || e) }; } })(); return true; } }; "
        + "try { new PerformanceObserver(list => { for (const e of list.getEntries()) p.longTasks.push(e.duration); }).observe({ type: 'longtask' }); p.observing = true; } catch { } return true; })()";
    /// <summary>The page's long tasks since the phase began: how many, their total and longest, and how many passed 100 and 250 ms.</summary>
    private const string PerfPageLongTasks = "JSON.stringify((() => { const d = window.__perf?.longTasks ?? []; const r = x => Math.round(x * 10) / 10; "
        + "return { observed: !!window.__perf?.observing, count: d.length, sumMs: r(d.reduce((a, b) => a + b, 0)), maxMs: r(Math.max(0, ...d)), over100: d.filter(x => x > 100).length, over250: d.filter(x => x > 250).length }; })())";

    private async Task RunPerfScenario()
    {
        var directory = options.ProfileDirectory ?? throw new InvalidOperationException("The perf scenario needs a profile.");
        var resultPath = Path.Combine(directory, "perf-result.json");
        var result = new Dictionary<string, object?> { ["completed"] = false, ["startedAt"] = DateTimeOffset.Now.ToString("O"), ["uiMode"] = "web", ["aiRequestSent"] = false };
        var phases = new Dictionary<string, object?>(); var errors = new List<string>(); result["phases"] = phases; result["errors"] = errors;
        void Write() => File.WriteAllText(resultPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        // The smoke's fixture providers: no CLI is read for a model list or a sign-in.
        runtime = new("win32", "smoke", true, "fixture", ProviderCatalog.Fallback("claude"), Wire.Providers.Select(p => new ProviderRuntime(p, ProviderCatalog.Name(p), true, "fixture", "isolated fixture", ProviderCatalog.Fallback(p), ProviderCatalog.Capabilities(p))).ToList(), null);
        var (workspaceId, seeded) = await SeedPerfScenario(directory);
        foreach (var (key, value) in seeded) result[key] = value;
        await ApplyLayoutPreset("tabs");
        var state = service.Snapshot;
        var active = state.ActiveSessionId!;
        var core = await (webShell ?? throw new InvalidOperationException("The perf scenario needs the web shell.")).Started ?? throw new InvalidOperationException("WebView2 did not start");
        await WaitUI(() => root.XamlRoot is not null && root.ActualWidth > 0, seconds: 30);
        var tabs = PaneLayout.Groups(EffectiveLayout(state, workspaceId)).First(g => g.SessionIds.Contains(active)).SessionIds.ToList();
        async Task<string> Js(string script) => await core.ExecuteScriptAsync(script);
        static string Literal(string text) => JsonSerializer.Serialize(text);
        // The page is ready when every tab of the group is drawn and the active pane shows.
        var ready = "(() => " + JsonSerializer.Serialize(tabs) + ".every(id => !!document.querySelector('[data-tab-id=\"' + id + '\"]')) && !!document.querySelector('.pane-slot.active:not(.hidden) .pane'))()";
        var deadline = Stopwatch.StartNew();
        while (await Js(ready) != "true") { if (deadline.Elapsed.TotalSeconds > 30) throw new TimeoutException("The page did not draw the scenario's tabs."); await Task.Delay(100); }
        await Js(PerfPageHelper);
        var agents = state.Sessions.Where(s => s.WorkspaceId == workspaceId && s.Kind == "claude").Select(s => s.Id).ToList();
        result["paneCount"] = agents.Count; result["tabCount"] = tabs.Count;
        result["totalLogEntries"] = state.Sessions.Sum(s => s.Logs.Count); result["activePaneLogEntries"] = state.Sessions.First(s => s.Id == active).Logs.Count;
        result["graphRuns"] = state.Sessions.Sum(s => s.GraphRuns?.Count ?? 0);
        result["windowSize"] = $"{root.ActualWidth:F0}x{root.ActualHeight:F0} @ {root.XamlRoot?.RasterizationScale ?? 1:F2}";
        Write();

        // The stall probe: each tick's lateness is the time since the last one less the 16 ms asked for. The system timer's
        // 15.6 ms step makes an idle tick some 15 ms late, which is why only lateness beyond 33 ms is summed.
        var probe = DispatcherQueue.CreateTimer(); probe.Interval = TimeSpan.FromMilliseconds(16); probe.IsRepeating = true;
        var lastTick = Stopwatch.GetTimestamp(); PerfStalls? stalls = null;
        probe.Tick += (_, _) => { var now = Stopwatch.GetTimestamp(); stalls?.Add(Stopwatch.GetElapsedTime(lastTick, now).TotalMilliseconds - 16); lastTick = now; };
        probe.Start();

        async Task Phase(string name, Func<List<double>, Dictionary<string, object?>, Task> body)
        {
            await Task.Delay(PerfIdleMs);
            var ops = new List<double>(); var extra = new Dictionary<string, object?>();
            var measured = stalls = new PerfStalls(); var watch = Stopwatch.StartNew();
            var (renders, tabFast, addFast) = (perfRenders, perfTabFast, perfAddFast);
            // The managed side's share: bytes allocated (boxing included), collections, and the time the GC held the threads.
            var (allocated, gen0, gen1, gen2, paused) = (GC.GetTotalAllocatedBytes(), GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), GC.GetTotalPauseDuration());
            PerfLog.Mark("perf phase " + name);
            await Js("window.__perf.longTasks.length = 0");
            try { await body(ops, extra); }
            finally { stalls = null; PerfLog.Mark("perf idle"); }
            extra["pageLongTasks"] = JsonDocument.Parse(JsonSerializer.Deserialize<string>(await Js(PerfPageLongTasks)) ?? "{}").RootElement.Clone();
            // Which paths the phase took: full layout rebuilds against the in-place tab switches and pane adds.
            extra["fullRenders"] = perfRenders - renders; extra["tabSwitchesInPlace"] = perfTabFast - tabFast; extra["paneAddsInPlace"] = perfAddFast - addFast;
            extra["capped"] = ops.Count(ms => ms >= PerfSettleCapMs - 50);
            extra["gc"] = new Dictionary<string, object> { ["allocatedMB"] = Math.Round((GC.GetTotalAllocatedBytes() - allocated) / 1048576.0, 1), ["gen0"] = GC.CollectionCount(0) - gen0, ["gen1"] = GC.CollectionCount(1) - gen1, ["gen2"] = GC.CollectionCount(2) - gen2, ["pauseMs"] = Math.Round((GC.GetTotalPauseDuration() - paused).TotalMilliseconds, 1) };
            var sorted = ops.Order().ToList();
            double Rank(double share) => sorted.Count == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(share * sorted.Count) - 1, 0, sorted.Count - 1)];
            var phase = new Dictionary<string, object?> { ["ops"] = ops, ["opMedian"] = Rank(.5), ["opP95"] = Rank(.95), ["opMax"] = sorted.LastOrDefault(), ["wallMs"] = Math.Round(watch.Elapsed.TotalMilliseconds), ["stall"] = measured.Json() };
            foreach (var (key, value) in extra) phase[key] = value;
            phases[name] = phase; Write();
        }
        // One operation in the page: the body acts on the DOM and awaits __perf.until(...) for its result; the page's time is the op's.
        async Task<double> PageOp(List<double> ops, string body)
        {
            error.Text = "";
            if (await Js("window.__perf.start(async (t0) => { " + body + " })") != "true") throw new InvalidOperationException("The page's perf helper is missing.");
            var waited = Stopwatch.StartNew(); JsonElement op = default;
            while (waited.ElapsedMilliseconds < PerfSettleCapMs + 1000)
            {
                op = JsonDocument.Parse(JsonSerializer.Deserialize<string>(await Js("JSON.stringify(window.__perfOp)")) ?? "null").RootElement.Clone();
                if (op.ValueKind == JsonValueKind.Object) break;
                await Task.Delay(15);
            }
            var ms = op.ValueKind == JsonValueKind.Object && op.TryGetProperty("ms", out var taken) ? Math.Round(taken.GetDouble(), 1) : PerfSettleCapMs;
            if (op.ValueKind == JsonValueKind.Object && op.TryGetProperty("error", out var failed)) { errors.Add("page: " + failed.GetString()); ms = Math.Max(ms, PerfSettleCapMs); }
            ops.Add(ms);
            if (error.Text.Length > 0) errors.Add(error.Text);
            return ms;
        }
        static string ClickTab(string id) => "const id = " + Literal(id) + "; const tab = document.querySelector('[data-tab-id=\"' + id + '\"] .tab-handle'); if (!tab) throw new Error('no tab ' + id); tab.click(); "
            + "await __perf.until(() => { const s = document.querySelector('.pane-slot[data-pane-id=\"' + id + '\"]'); return !!s && !s.classList.contains('hidden') && !!s.querySelector('.pane') && !!s.querySelector('[data-testid=conversation], [data-testid=mighty]'); }, t0);";
        static string TypeDraft(string text) => "const text = " + Literal(text) + "; const e = __perf.slot()?.querySelector('[data-testid=composer-input]') ?? document.querySelector('[data-testid=composer-input]'); if (!e) throw new Error('no composer'); "
            + "Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value').set.call(e, text); e.dispatchEvent(new Event('input', { bubbles: true })); await __perf.until(() => e.value === text, t0);";
        async Task SetViews(IEnumerable<string> ids, string mode)
        {
            var set = ids.ToHashSet();
            await service.UpdateAsync(s => s with { Sessions = s.Sessions.Select(p => set.Contains(p.Id) ? MightyGraphViewModel.ApplyViewMode(p, mode) : p).ToList() });
            await SettledAsync();
        }
        async Task SwitchTabs(List<double> ops)
        {
            var at = tabs.IndexOf(service.Snapshot.ActiveSessionId ?? active);
            for (var round = 0; round < PerfRounds; round++) await PageOp(ops, ClickTab(tabs[(at + 1 + round) % tabs.Count]));
            await PageOp([], ClickTab(active)); await SettledAsync();
        }

        await SetViews(agents, "default");
        await Phase("switchTabsBasic", (ops, _) => SwitchTabs(ops));
        await SetViews(agents, "mighty");
        await Phase("switchTabsMighty", (ops, _) => SwitchTabs(ops));
        await Phase("toggleView", async (ops, _) =>
        {
            // The pane header's Default | Mighty switch: the click, the host's save, and the page drawing the other view.
            const string Toggle = "const s = __perf.slot(); const next = s?.querySelector('[data-testid=mighty-mode-switch] button.on')?.dataset.mode === 'mighty' ? 'default' : 'mighty'; "
                + "const b = s?.querySelector('[data-testid=mighty-mode-switch] button[data-mode=' + next + ']'); if (!b) throw new Error('no view switch'); b.click(); "
                + "await __perf.until(() => { const c = __perf.slot(); return next === 'mighty' ? !!c?.querySelector('[data-testid=mighty]') : !!c?.querySelector('[data-testid=conversation]') && !c.querySelector('[data-testid=mighty]'); }, t0);";
            for (var turn = 0; turn < PerfToggles; turn++) await PageOp(ops, Toggle);
        });
        await Phase("addPane", async (ops, extra) =>
        {
            var before = service.Snapshot.Sessions.Select(s => s.Id).ToHashSet();
            // The page's add-pane menu: the sidebar's + opens it, its Claude entry adds a pane, and the op ends when the new pane shows.
            const string Add = "const known = new Set([...document.querySelectorAll('[data-testid=tab]')].map(e => e.dataset.tabId)); const entry = () => document.querySelector('[data-testid=\"add-entry-agent:claude\"]'); "
                + "const plus = document.querySelector('[data-testid=sidebar-add]'); if (!plus) throw new Error('no add button'); plus.click(); await __perf.until(() => !!entry(), t0); entry().click(); "
                + "await __perf.until(() => { const s = __perf.slot(); return !!s && !known.has(s.dataset.paneId) && !!s.querySelector('.pane'); }, t0);";
            for (var count = 0; count < PerfAddedPanes; count++) await PageOp(ops, Add);
            var added = service.Snapshot.Sessions.Where(s => !before.Contains(s.Id)).Select(s => s.Id).ToList();
            var closes = new List<double>();
            static string Close(string id) => "const id = " + Literal(id) + "; const b = document.querySelector('[data-tab-id=\"' + id + '\"] .tab-close'); if (!b) throw new Error('no close for ' + id); b.click(); "
                + "await __perf.until(() => !document.querySelector('[data-tab-id=\"' + id + '\"]'), t0);";
            foreach (var id in added) await PageOp(closes, Close(id));
            extra["closeOps"] = closes; extra["added"] = added.Count;
            await PageOp([], ClickTab(active));
            await SettledAsync();
        });
        foreach (var (name, mode) in new[] { ("streamBasic", "default"), ("streamMighty", "mighty") })
        {
            await SetViews([active], mode);
            await Phase(name, (ops, extra) => StreamPerfAnswer(active, name, extra, text => PageOp(ops, TypeDraft(text))));
            await PageOp([], TypeDraft(""));
        }
        // Natural submit B1: Enter in the composer until the page draws the sent request (the conversation's bubble, the diagram's
        // request block), before the host has answered; the target is one frame, 50 ms or less. The start goes to the smoke's
        // stand-in, which puts the request's graph run and user entry on the pane as a start does (a diagram pane's stand-in waits
        // for both), so the page's block gives way to them before the next send. Nothing reaches a CLI.
        static string SendAndSee() => "const e = __perf.slot()?.querySelector('[data-testid=composer-input]'); if (!e) throw new Error('no composer'); "
            + "const drawn = () => [...(__perf.slot() ?? document).querySelectorAll('[data-pending-request]')].map(x => x.dataset.pendingRequest); const before = new Set(drawn()); "
            + "e.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', code: 'Enter', keyCode: 13, bubbles: true, cancelable: true })); "
            + "await __perf.until(() => drawn().some(id => !before.has(id)), t0);";
        foreach (var (name, mode) in new[] { ("sendBlockBasic", "default"), ("sendBlockMighty", "mighty") })
        {
            await SetViews([active], mode);
            var standIn = smokeStart;
            smokeStart = async request =>
            {
                var graph = new MightyGraphRun { Id = Wire.Id(), Input = request.Input, Status = "completed", Provider = request.Provider };
                await service.UpdateAsync(s => s with { Sessions = s.Sessions.Select(p => p.Id == request.SessionId ? p with { GraphRuns = [.. p.GraphRuns ?? [], graph] } : p).ToList() });
                await PerfReceive(new RunEvent(request.SessionId, "log", new LogEntry(Wire.Id(), "user", request.Input, Wire.Now(), request.Provider)));
            };
            try
            {
                await Phase(name, async (ops, extra) =>
                {
                    for (var turn = 1; turn <= PerfSends; turn++)
                    {
                        await PageOp([], TypeDraft("perf send " + name + " " + turn));
                        await PageOp(ops, SendAndSee());
                        await SettledAsync();
                    }
                    extra["sends"] = PerfSends; extra["targetMs"] = 50;
                });
            }
            finally { smokeStart = standIn; }
        }
        probe.Stop();
        result["perfLog"] = Path.Combine(directory, "perf.log");
        result["perfLogSteps"] = PerfLogSteps(Path.Combine(directory, "perf.log"));
        result["completed"] = true; result["finishedAt"] = DateTimeOffset.Now.ToString("O");
        Write();
    }

    /// <summary>
    /// A streamed answer on <paramref name="sessionId"/>, through the service's own run-event path (its private <c>Receive</c>, from
    /// a pool thread as a run's reader calls it): the state applies each event, a save is scheduled, and every RunEventReceived
    /// handler — the window's batched pane refresh, the diagram's — runs as for a real run. A running request is put on the pane
    /// first so the diagram has a live block that follows the answer. Every <see cref="PerfTypingEveryMs"/> the page's composer is
    /// typed into through <paramref name="pageType"/>, which times it as the phase's operation.
    /// </summary>
    private async Task StreamPerfAnswer(string sessionId, string name, Dictionary<string, object?> extra, Func<string, Task<double>> pageType)
    {
        Task Send(RunEvent value) => PerfReceive(value);
        var run = new MightyGraphRun { Id = "perf-" + name, Input = "Perf: " + name, Status = "running", Provider = "claude" };
        await service.UpdateAsync(s => s with { Sessions = s.Sessions.Select(p => p.Id == sessionId ? p with { GraphRuns = [.. p.GraphRuns ?? [], run] } : p).ToList() });
        await Send(new RunEvent(sessionId, "log", new LogEntry(Wire.Id(), "user", "Perf: " + name, Wire.Now(), "claude")));
        await Send(RunEvent.State(sessionId, "running"));
        var entry = Wire.Id(); var text = new System.Text.StringBuilder(); var total = PerfStreamSeconds * PerfEventsPerSecond; var gap = 1000.0 / PerfEventsPerSecond;
        var clock = Stopwatch.StartNew(); var sent = 0; var lateSends = 0;
        async Task Stream()
        {
            for (var index = 0; index < total; index++)
            {
                var wait = index * gap - clock.Elapsed.TotalMilliseconds;
                if (wait > 1) await Task.Delay(TimeSpan.FromMilliseconds(wait)); else if (wait < -gap) lateSends++;
                text.Append(index % 25 == 0 ? $"\n### Section {index / 25 + 1}\n" : index % 7 == 0 ? $"- item {index} with `code` and **bold** words\n" : $"Line {index}: the answer goes on with a sentence long enough to wrap in a narrow pane.\n");
                await Send(new RunEvent(sessionId, "log", new LogEntry(entry, "assistant", text.ToString(), Wire.Now(), "claude"))); sent++;
            }
        }
        async Task Type()
        {
            for (var turn = 1; clock.Elapsed.TotalMilliseconds < PerfStreamSeconds * 1000 - PerfTypingEveryMs; turn++)
            {
                var wait = turn * PerfTypingEveryMs - clock.Elapsed.TotalMilliseconds;
                if (wait > 1) await Task.Delay(TimeSpan.FromMilliseconds(wait));
                await pageType("perf draft " + turn);
            }
        }
        await Task.WhenAll(Stream(), Type());
        await Send(RunEvent.State(sessionId, "completed"));
        extra["events"] = sent; extra["eventsLateOverOneGap"] = lateSends; extra["streamMs"] = Math.Round(clock.Elapsed.TotalMilliseconds);
        await SettledAsync();
        // The request is put down as finished, so the next phase's diagram has nothing running.
        var final = text.ToString();
        await service.UpdateAsync(s => s with { Sessions = s.Sessions.Select(p => p.Id == sessionId && p.GraphRuns is { Count: > 0 } runs ? p with { GraphRuns = [.. runs.Select(r => r.Id == run.Id ? new MightyGraphRun { Id = r.Id, Input = r.Input, Status = "completed", Provider = "claude", RootEntries = r.RootEntries, FinalOutput = final } : r)] } : p).ToList() });
    }

    /// <summary>One run event through the service's own path (its private <c>Receive</c>, from a pool thread as a run's reader calls it).</summary>
    private Task PerfReceive(RunEvent value)
    {
        var receive = typeof(DesktopService).GetMethod("Receive", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(RunEvent)]) ?? throw new MissingMethodException(nameof(DesktopService), "Receive");
        return Task.Run(() => receive.Invoke(service, [value]));
    }

    /// <summary>The scenario's workspace and panes: the copied state's local agent panes, then made-up ones until the busiest workspace has four Claude panes.</summary>
    private async Task<(string WorkspaceId, Dictionary<string, object?> Facts)> SeedPerfScenario(string directory)
    {
        var facts = new Dictionary<string, object?>();
        var folders = Path.Combine(directory, "perf-workspaces");
        async Task<string> Folder(string name)
        {
            var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
            var path = Path.Combine(folders, $"{(Directory.Exists(folders) ? Directory.GetDirectories(folders).Length : 0):00} {(safe.Length == 0 ? "workspace" : safe)}");
            Directory.CreateDirectory(path);
            return (await service.AddWorkspaceAsync(path)).Id;
        }
        var imported = new List<RunSession>();
        if (Environment.GetEnvironmentVariable("MIGHTY_PERF_STATE") is { Length: > 0 } source)
        {
            // Read from a copy, so the scenario never opens the given folder's state for writing; every workspace becomes a folder in
            // the profile (no project of the user's is read) and only agent panes come over (a terminal or browser pane would start a process).
            var copy = Path.Combine(directory, "perf-import"); Directory.CreateDirectory(copy);
            File.Copy(Path.Combine(source, "workspace-state.json"), Path.Combine(copy, "workspace-state.json"));
            var snapshot = await new StateStore(copy).LoadAsync();
            var ids = new Dictionary<string, string>();
            // A request the copy caught running would keep its block animating through every phase: it is put down as stopped.
            foreach (var run in snapshot.Sessions.SelectMany(s => s.GraphRuns ?? []).Where(r => !MightyGraphSupport.Terminal(r.Status))) run.Status = "stopped";
            foreach (var workspace in snapshot.Workspaces.Where(w => w.Remote is null)) ids[workspace.Id] = await Folder(workspace.Name);
            imported = snapshot.Sessions.Where(s => s.Kind == "claude" && ids.ContainsKey(s.WorkspaceId)).Select(s => s with
            {
                WorkspaceId = ids[s.WorkspaceId], Status = s.Status is "idle" or "completed" or "error" or "stopped" ? s.Status : "completed",
                Draft = "", CurrentActivity = null, ParentSessionId = null, WorkingFolder = null
            }).ToList();
            await service.UpdateAsync(s => s with { Sessions = [.. s.Sessions, .. imported] });
            facts["importedFrom"] = source; facts["importedWorkspaces"] = ids.Count; facts["importedPanes"] = imported.Count;
        }
        var workspaceId = imported.GroupBy(s => s.WorkspaceId).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault() ?? await Folder("Perf project");
        var claude = service.Snapshot.Sessions.Count(s => s.WorkspaceId == workspaceId && s.Kind == "claude" && s.Provider == "claude");
        // With the owner's state, only the owner's panes are measured; made-up panes fill in only a run without one.
        var made = imported.Count > 0 ? [] : Enumerable.Range(0, Math.Max(0, 4 - claude)).Select(index => PerfPane(workspaceId, index)).ToList();
        if (made.Count > 0) await service.UpdateAsync(s => s with { Sessions = [.. s.Sessions, .. made] });
        facts["synthesizedPanes"] = made.Count;
        // The busiest conversation is the one every phase works on.
        var active = service.Snapshot.Sessions.Where(s => s.WorkspaceId == workspaceId && s.Kind == "claude").OrderByDescending(s => s.Logs.Count).First().Id;
        await service.UpdateAsync(s => s with { ActiveWorkspaceId = workspaceId, ActiveSessionId = active });
        return (workspaceId, facts);
    }

    /// <summary>A made-up Claude pane: 300 entries (requests, Markdown answers with headings, lists and code, tool calls) and 15 finished requests for the diagram.</summary>
    private static RunSession PerfPane(string workspaceId, int pane)
    {
        var logs = new List<LogEntry>(); var runs = new List<MightyGraphRun>();
        for (var turn = 0; turn < 75; turn++)
        {
            var request = $"Pane {pane + 1}, request {turn + 1}: look at the module and fix what is slow.";
            var answer = $"## Step {turn + 1}\n\nI read the code and changed **three** things:\n\n- the cache now keeps `{turn}` entries\n- the loop no longer copies the list\n- a test covers it\n\n```csharp\nfor (var i = 0; i < {turn}; i++)\n    total += values[i];\n```\n\n> The rest stays as it was.";
            var tool = new AgentActivity($"perf-{pane}-{turn}", "claude", "command", "completed", "dotnet test", Output: "42 tests passed", DurationMs: 1200 + turn);
            var entries = new List<LogEntry> { new(Wire.Id(), "user", request, Wire.Now(), "claude"), new(Wire.Id(), "system", tool.Summary, Wire.Now(), "claude", tool), new(Wire.Id(), "assistant", answer, Wire.Now(), "claude"), new(Wire.Id(), "assistant", $"Done with step {turn + 1}.", Wire.Now(), "claude") };
            logs.AddRange(entries);
            if (turn >= 60) runs.Add(new MightyGraphRun { Id = $"perf-run-{pane}-{turn}", Input = request, Status = "completed", Provider = "claude", RootEntries = entries.Skip(1).ToList(), FinalOutput = answer });
        }
        return new RunSession { WorkspaceId = workspaceId, Provider = "claude", Title = $"Perf {pane + 1}", Status = "completed", Logs = logs, GraphRuns = runs };
    }

    /// <summary>
    /// Waits until the UI has settled: a low-priority callback runs after everything queued before it, and then one frame is drawn.
    /// A frame that does not come within <see cref="PerfSettleCapMs"/> (a minimized window draws none) ends the wait.
    /// </summary>
    private Task SettledAsync()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            EventHandler<object>? frame = null;
            frame = (_, _) => { CompositionTarget.Rendering -= frame; done.TrySetResult(); };
            CompositionTarget.Rendering += frame;
        })) done.TrySetResult();
        return Task.WhenAny(done.Task, Task.Delay(PerfSettleCapMs));
    }

    /// <summary>perf.log's timed steps, added up per scenario phase (the "perf phase" marks) and for the whole run.</summary>
    private static Dictionary<string, Dictionary<string, object>> PerfLogSteps(string path)
    {
        var totals = new Dictionary<string, Dictionary<string, (int Count, double Total, double Max)>>();
        if (!File.Exists(path)) return [];
        var phase = "setup";
        foreach (var line in File.ReadLines(path))
        {
            if (Regex.Match(line, @"^\S+\s+mark\s+perf phase (.+)$") is { Success: true } begin) { phase = begin.Groups[1].Value.Trim(); continue; }
            if (Regex.IsMatch(line, @"^\S+\s+mark\s+perf idle$")) { phase = "idle"; continue; }
            if (Regex.Match(line, @"^\S+\s+([\d.,]+) ms (.+)$") is not { Success: true } step || !double.TryParse(step.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var ms)) continue;
            var what = step.Groups[2].Value.Trim();
            foreach (var bucket in new[] { phase, "all" })
            {
                if (!totals.TryGetValue(bucket, out var steps)) totals[bucket] = steps = [];
                var (count, sum, max) = steps.GetValueOrDefault(what);
                steps[what] = (count + 1, sum + ms, Math.Max(max, ms));
            }
        }
        return totals.ToDictionary(p => p.Key, p => p.Value.OrderByDescending(s => s.Value.Total).ToDictionary(s => s.Key, s => (object)new { count = s.Value.Count, totalMs = Math.Round(s.Value.Total, 1), maxMs = Math.Round(s.Value.Max, 1) }));
    }
}
