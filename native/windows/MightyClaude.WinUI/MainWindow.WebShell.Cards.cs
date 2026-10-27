using System.Diagnostics;
using System.Text.Json;
using MightyClaude.Core;

namespace MightyClaude.WinUI;

// What the page draws above a pane's composer, and its status line (native/windows/web/src/cards), as the never-shown WinUI
// pane view holds them (MainWindow.WebShell.Composer.cs): read from the same requests, drafts and Core helpers the WinUI cards
// read, pushed as one `cards` message per pane when they change, and answered through the same C# methods.

/// <summary>The next-action rows (MainWindow.NextActions.cs): the labels of the latest reply's options.</summary>
internal sealed record WebNextActionsCard(string EntryId, IReadOnlyList<string> Actions) { public string Kind => "nextActions"; }

/// <summary>One answered plan of the history strip (MainWindow.PlanCard.cs PlanRecordRow).</summary>
internal sealed record WebPlanRecord(string Id, string OutcomeTitle, string Tone, string Time, string Headline, string Plan, bool Truncated, string? Feedback);
internal sealed record WebPlanHistoryCard(IReadOnlyList<WebPlanRecord> Records) { public string Kind => "planHistory"; }

/// <summary>
/// One background task row: kind · status · elapsed · notice, already joined (PlanCardSupport.BackgroundStripRow.Detail), its kind
/// words the diagram block's (하위 에이전트 · 응답 대기, 백그라운드 실행 · 알림으로 확인) when the row meets one; <c>Tail</c> is a background
/// run's last output line, absent otherwise.
/// </summary>
internal sealed record WebBackgroundTask(string Text, string Status, string Detail, string? Tail = null);
internal sealed record WebBackgroundCard(int Count, string Summary, bool Open, IReadOnlyList<WebBackgroundTask> Tasks) { public string Kind => "background"; }

/// <summary>The generic tool permission card (MainWindow.ToolPermission.cs RenderToolPermissionCards), its words the host's.</summary>
internal sealed record WebPermissionField(string Label, string Value, bool Code);
internal sealed record WebPermissionCard(string RequestId, string Title, string Tool, string? Waiting, string? Headline, IReadOnlyList<WebPermissionField> Fields, string? Summary, string? Path,
    string? Reason, string? CannotAllow, bool CanAllow, string InputJson, string RawJson, string OnceOnly, string Deny, string AllowOnce) { public string Kind => "permission"; }

/// <summary>An AskUserQuestion request (MainWindow.Questionnaire.cs); the page holds the step and the picks, the host checks and sends them.</summary>
internal sealed record WebQuestionOption(string Label, string Description);
internal sealed record WebQuestion(string Header, string Question, bool MultiSelect, IReadOnlyList<WebQuestionOption> Options);
internal sealed record WebQuestionnaireCard(string RequestId, int Count, IReadOnlyList<WebQuestion> Questions) { public string Kind => "questionnaire"; }

/// <summary>Claude's plan (MainWindow.PlanCard.cs): the whole card, or only its answers while the diagram draws the plan.</summary>
internal sealed record WebPlanCard(string RequestId, int Count, string Received, string Plan, bool ActionsOnly, bool Sending, string? Error) { public string Kind => "plan"; }

/// <summary>The Antigravity card (MainWindow.AntigravityPermission.cs AntigravityCard).</summary>
internal sealed record WebAntigravityAction(string Heading, string? Target, bool Code, string? Rule, string? OnceOnly);
internal sealed record WebAntigravityCard(string CardId, string RunId, IReadOnlyList<WebAntigravityAction> Actions, string? Reason, bool Busy, bool CanAllow, bool CanAllowAlways, string? AlwaysTip) { public string Kind => "antigravity"; }

/// <summary>An agent's request to open a web page (MainWindow.AgentWebPrompts.cs).</summary>
internal sealed record WebOpenCard(string RequestId, string Url) { public string Kind => "webOpen"; }

/// <summary>The sign-in card after an auth error (MainWindow.LoginRecovery.cs RenderLoginRecovery): its lines and which buttons it has.</summary>
internal sealed record WebLoginLine(string Text, string Tone, bool Busy);
internal sealed record WebLoginCard(string Title, IReadOnlyList<WebLoginLine> Lines, string? Url, bool AsksForCode, IReadOnlyList<string> Buttons, bool LoginEnabled) { public string Kind => "login"; }

/// <summary>A status line run: its text, an RGB colour or an ink token, and the terminal's attributes (MainWindow.StatusLine.cs Row).</summary>
internal sealed record WebStatusSegment(string Text, string? Color, string? Ink, bool Dim, bool Bold, bool Italic, bool Underline);
internal sealed record WebStatusTrust(string Prompt, string Command, string Allow, string Deny, string Note);
internal sealed record WebStatusLineCard(IReadOnlyList<IReadOnlyList<WebStatusSegment>> Lines, string? Error, WebStatusTrust? Trust, string Tip, string Label) { public string Kind => "statusLine"; }

public sealed partial class MainWindow
{
    private readonly HashSet<string> webCardsDirty = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> webCardsSent = new(StringComparer.Ordinal);
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? webCardsFlush, webCardsTick;

    /// <summary>A pane's cards may have changed (a request, a refresh, a sign-in, the status line): a push follows within a frame or two.</summary>
    private void WebCardsChanged(PaneView view)
    {
        if (webShell is null) return;
        webCardsDirty.Add(view.PaneId);
        if (webCardsFlush is null)
        {
            webCardsFlush = DispatcherQueue.CreateTimer(); webCardsFlush.Interval = TimeSpan.FromMilliseconds(30); webCardsFlush.IsRepeating = false;
            webCardsFlush.Tick += (_, _) => FlushWebCards();
        }
        if (!webCardsFlush.IsRunning) webCardsFlush.Start();
    }

    /// <summary>A request for an agent pane the page has not shown yet: its never-shown view is made now, so the card waits there.</summary>
    private bool WebPaneForRequest(ToolPermissionRequest request, out PaneView? pane)
    {
        pane = null;
        if (webShell is null || request.State != "pending" || !service.Snapshot.Sessions.Any(s => s.Id == request.RunId && s.Kind == "claude")) return false;
        pane = WebPane(request.RunId);
        return true;
    }

    private void FlushWebCards()
    {
        if (webShell is null) return;
        var ticking = false;
        foreach (var id in webCardsDirty.ToArray())
        {
            webCardsDirty.Remove(id);
            if (!views.TryGetValue(id, out var view) || !service.Snapshot.Sessions.Any(s => s.Id == id)) { webCardsSent.Remove(id); continue; }
            var cards = view.WebCards(out var ticks); ticking |= ticks;
            var json = JsonSerializer.Serialize(cards, Wire.Json);
            if (webCardsSent.TryGetValue(id, out var last) && last == json) continue;
            webCardsSent[id] = json; webShell.Bridge.Push(new { kind = "cards", paneId = id, cards });
        }
        // An open background strip with a task running says its elapsed time again each second, as the WinUI rows tick.
        if (ticking)
        {
            if (webCardsTick is null)
            {
                webCardsTick = DispatcherQueue.CreateTimer(); webCardsTick.Interval = TimeSpan.FromSeconds(1); webCardsTick.IsRepeating = false;
                webCardsTick.Tick += (_, _) => { foreach (var id in webCardsSent.Keys) if (views.TryGetValue(id, out var view)) WebCardsChanged(view); };
            }
            if (!webCardsTick.IsRunning) webCardsTick.Start();
        }
    }

    /// <summary>
    /// The cards' answers. Each goes through the method the WinUI card's button calls; a refusal throws (the page shows its
    /// message) or comes back as the words the card shows.
    /// </summary>
    private void RegisterCardMethods(WebBridge bridge)
    {
        bridge.Register("cards", args => Task.FromResult<object?>(WebPane(args.Text("paneId")).WebCards(out _)));
        bridge.Register("permissionAnswer", args => { WebPane(args.Text("paneId")).WebAnswerPermission(args.Text("requestId") ?? "", Flag(args, "allow")); return Task.FromResult<object?>(null); });
        bridge.Register("questionnaireAnswer", args =>
        {
            var answers = new List<(IReadOnlyList<string> Picks, string? Custom)>();
            if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("answers", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var answer in list.EnumerateArray())
                {
                    var picks = answer.TryGetProperty("picks", out var chosen) && chosen.ValueKind == JsonValueKind.Array ? chosen.EnumerateArray().Select(p => p.GetString() ?? "").ToList() : [];
                    answers.Add((picks, answer.Text("custom")));
                }
            return Task.FromResult<object?>(WebPane(args.Text("paneId")).WebAnswerQuestionnaire(args.Text("requestId") ?? "", answers));
        });
        bridge.Register("planAnswer", async args => { await WebPane(args.Text("paneId")).WebAnswerPlan(args.Text("requestId") ?? "", args.Text("decision") ?? "", args.Text("feedback")); return null; });
        bridge.Register("antigravityAnswer", async args =>
        {
            if (!Enum.TryParse<AntigravityPermissionDecision>(args.Text("decision"), ignoreCase: true, out var decision)) throw new ArgumentException("Unknown decision.");
            return await WebPane(args.Text("paneId")).WebAnswerAntigravity(args.Text("cardId") ?? "", decision);
        });
        bridge.Register("webOpenChoose", args =>
        {
            agentUrlPrompts.Choose(args.Text("requestId") ?? "", args.Text("destination") == "external" ? "external" : "inApp", Flag(args, "remember"));
            return Task.FromResult<object?>(null);
        });
        bridge.Register("loginAction", async args => { await WebPane(args.Text("paneId")).WebLoginAction(args.Text("action") ?? "", args.Text("code")); return null; });
        bridge.Register("backgroundWork", async args => { await WebPane(args.Text("paneId")).WebBackgroundWork(args.Text("action") ?? ""); return null; });
        bridge.Register("nextActionFill", args => Task.FromResult<object?>(WebPane(args.Text("paneId")).WebNextActionFill(args.Text("entryId") ?? "", args.TryGetProperty("index", out var at) && at.TryGetInt32(out var n) ? n : -1)));
        bridge.Register("statusLineTrust", async args => { await WebPane(args.Text("paneId")).WebStatusLineTrust(Flag(args, "allow")); return null; });
    }

    /// <summary>
    /// The cards' part of the web smoke: a fixture tool permission and a questionnaire are put on the pane (as
    /// <see cref="PaneView.RunToolPermissionSmoke"/> does); the page must draw them, and Allow, then the question's answer and
    /// Send, pressed in the page, must come back through the host's answers (smokePermissionResponses), never the CLI.
    /// </summary>
    private async Task<bool> RunWebCardsSmoke(Microsoft.Web.WebView2.Core.CoreWebView2 core, string paneId, Dictionary<string, object?> result)
    {
        var view = WebPane(paneId);
        var responses = smokePermissionResponses = [];
        async Task<bool> Wait(string script, int seconds = 15)
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < deadline) { if (await core.ExecuteScriptAsync(script) == "true") return true; await Task.Delay(100); }
            return false;
        }
        async Task<bool> WaitHost(Func<bool> done)
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline && !done()) await Task.Delay(100);
            return done();
        }
        const string Click = "(s => { const e = document.querySelector(s); e?.click(); return !!e; })";
        try
        {
            view.ReceiveToolPermission(new ToolPermissionRequest("web-smoke-perm", paneId, "web-smoke-tuid-1", "Bash", "{\"command\":\"ls /tmp\"}", "Run command"));
            var permission = await Wait("!!document.querySelector('[data-card=permission] [data-answer=allow]')");
            var tool = await core.ExecuteScriptAsync("document.querySelector('[data-card=permission] .card-tool')?.textContent ?? null");
            await core.ExecuteScriptAsync(Click + "('[data-card=permission] [data-answer=allow]')");
            var allowed = await WaitHost(() => responses.TryGetValue("web-smoke-perm", out var allow) && allow);
            result["cardsPermission"] = new { shown = permission, tool = JsonSerializer.Deserialize<string?>(tool), allowed };

            const string Input = "{\"questions\":[{\"header\":\"Color\",\"question\":\"Which colour?\",\"multiSelect\":false,\"options\":[{\"label\":\"Red\",\"description\":\"warm\"},{\"label\":\"Blue\",\"description\":\"cool\"}]}]}";
            view.ReceiveToolPermission(new ToolPermissionRequest("web-smoke-question", paneId, "web-smoke-tuid-2", "AskUserQuestion", Input, "Ask", CanAllow: false, CanAnswerQuestions: true));
            var asked = await Wait("!!document.querySelector('[data-card=questionnaire] [data-option=\"0\"]')");
            await core.ExecuteScriptAsync(Click + "('[data-card=questionnaire] [data-option=\"1\"]')");
            var submit = await Wait("(() => { const e = document.querySelector('[data-card=questionnaire] [data-answer=submit]'); return !!e && !e.disabled; })()", 5);
            await core.ExecuteScriptAsync(Click + "('[data-card=questionnaire] [data-answer=submit]')");
            var answered = await WaitHost(() => responses.ContainsKey("web-smoke-question"));
            var gone = await Wait("!document.querySelector('[data-card=questionnaire]') && !document.querySelector('[data-card=permission]')", 5);
            result["cardsQuestionnaire"] = new { shown = asked, submit, answered, picked = view.WebSmokeAnswer, gone };
            var keyed = await RunWebCardKeysSmoke(core, paneId, result);
            return permission && allowed && asked && submit && answered && view.WebSmokeAnswer == "Blue" && gone && keyed;
        }
        finally
        {
            smokePermissionResponses = null;
            view.ClearToolPermissions();
        }
    }

    /// <summary>
    /// The cards' keyboard as a person uses it (src/cards/keys.ts, WaitCard): pressed with the mouse and real key events
    /// (CDP Input.*, into this app's own WebView only), on a question, a permission and a plan card. A card arriving leaves the
    /// composer's keyboard alone; a press on an answer or on the card's words puts the keyboard on the card; the arrows, Home
    /// and End move it; Enter picks an answer and goes on to the forward button, Space only picks, Enter presses a button, and
    /// Enter in the change request's box writes a new line. Each step's focus is kept in <c>cardsKeys</c>.
    /// </summary>
    private async Task<bool> RunWebCardKeysSmoke(Microsoft.Web.WebView2.Core.CoreWebView2 core, string paneId, Dictionary<string, object?> result)
    {
        var view = WebPane(paneId); var responses = smokePermissionResponses ?? [];
        var steps = new List<object>(); var ok = true;
        var plans = new List<PlanDecision>(); var previousPlan = smokePlanAnswerer;
        async Task<string> Eval(string script) => await core.ExecuteScriptAsync(script);
        async Task<string?> Active() => JsonSerializer.Deserialize<string?>(await Eval("(() => { const e = document.activeElement; return !e || e === document.body ? 'body' : e.dataset.answer ?? e.dataset.testid ?? e.className ?? e.tagName; })()"));
        async Task<bool> Until(string script, int seconds = 3)
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < deadline) { if (await Eval(script) == "true") return true; await Task.Delay(50); }
            return false;
        }
        async Task Check(string step, string script)
        {
            var passed = await Until(script);
            steps.Add(new { step, passed, active = await Active() });
            ok &= passed;
        }
        Task Focused(string step, string answer) => Check(step, $"document.activeElement?.dataset?.answer === '{answer}'");
        async Task Mouse(string selector)
        {
            var at = JsonSerializer.Deserialize<string>(await Eval("(() => { const e = document.querySelector(" + JsonSerializer.Serialize(selector) + "); if (!e) return '{}'; e.scrollIntoView({ block: 'nearest' }); "
                + "const r = e.getBoundingClientRect(), x = r.left + Math.min(10, r.width / 2), y = r.top + r.height / 2; const hit = document.elementFromPoint(x, y); "
                + "return JSON.stringify({ x, y, hit: e.contains(hit) }); })()")) ?? "{}";
            using var point = JsonDocument.Parse(at);
            if (!point.RootElement.TryGetProperty("x", out var x) || !point.RootElement.TryGetProperty("y", out var y)) { steps.Add(new { step = "mouse", passed = false, selector }); ok = false; return; }
            if (!point.RootElement.TryGetProperty("hit", out var hit) || !hit.GetBoolean()) { steps.Add(new { step = "mouseCovered", passed = false, selector }); ok = false; }
            foreach (var (type, buttons) in new[] { ("mouseMoved", 0), ("mousePressed", 1), ("mouseReleased", 0) })
                await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type, x = x.GetDouble(), y = y.GetDouble(), button = type == "mouseMoved" ? "none" : "left", buttons, clickCount = type == "mouseMoved" ? 0 : 1 }));
        }
        // A real key: down (with its character for Enter and Space, as a keyboard's keypress) and up.
        async Task Key(string key, string code, int virtualKey, string? text = null, int modifiers = 0)
        {
            var events = new List<string>();
            foreach (var type in new[] { text is null ? "rawKeyDown" : "keyDown", "keyUp" })
            {
                var fields = new Dictionary<string, object> { ["type"] = type, ["key"] = key, ["code"] = code, ["windowsVirtualKeyCode"] = virtualKey, ["nativeVirtualKeyCode"] = virtualKey, ["modifiers"] = modifiers };
                if (text is not null && type == "keyDown") { fields["text"] = text; fields["unmodifiedText"] = text; }
                await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", JsonSerializer.Serialize(fields));
            }
            await Task.Delay(60);
        }
        Task Down() => Key("ArrowDown", "ArrowDown", 0x28);
        Task Up() => Key("ArrowUp", "ArrowUp", 0x26);
        Task Right() => Key("ArrowRight", "ArrowRight", 0x27);
        Task Left() => Key("ArrowLeft", "ArrowLeft", 0x25);
        Task Home() => Key("Home", "Home", 0x24);
        Task End() => Key("End", "End", 0x23);
        Task Enter() => Key("Enter", "Enter", 0x0D, "\r");
        Task Space() => Key(" ", "Space", 0x20, " ");
        Task Tab() => Key("Tab", "Tab", 0x09);
        const string Composer = "document.activeElement?.dataset?.testid === 'composer-input'";
        try
        {
            smokePlanAnswerer = (_, _, decision) => plans.Add(decision);
            // A question of two steps arrives while the composer has the keyboard: it stays there.
            await Eval("document.querySelector('[data-testid=composer-input]')?.focus()");
            const string Input = "{\"questions\":[{\"header\":\"Color\",\"question\":\"Which colour?\",\"multiSelect\":false,\"options\":[{\"label\":\"Red\",\"description\":\"warm\"},{\"label\":\"Blue\",\"description\":\"cool\"}]},"
                + "{\"header\":\"Sides\",\"question\":\"Which sides?\",\"multiSelect\":true,\"options\":[{\"label\":\"Left\",\"description\":\"\"},{\"label\":\"Right\",\"description\":\"\"}]}]}";
            view.ReceiveToolPermission(new ToolPermissionRequest("web-keys-question", paneId, "web-keys-tuid-1", "AskUserQuestion", Input, "Ask", CanAllow: false, CanAnswerQuestions: true));
            ok &= await Until("!!document.querySelector('[data-card=questionnaire] [data-option=\"0\"]')", 15);
            await Check("questionArrivesComposerKept", Composer);
            // Pressed on an answer: the keyboard is on it (and it is picked); the arrows move, Home and End go to the ends.
            await Mouse("[data-card=questionnaire] [data-answer=option-0-0]");
            await Focused("mouseOnOption", "option-0-0");
            await Check("mousePicked", "document.querySelector('[data-answer=option-0-0]')?.getAttribute('aria-checked') === 'true'");
            await Down(); await Focused("arrowDown", "option-0-1");
            await Up(); await Focused("arrowUp", "option-0-0");
            await End(); await Focused("end", "next");
            await Home(); await Focused("home", "step-0");
            await Right(); await Focused("arrowRight", "step-1");
            await End(); await Up(); await Up(); await Up(); await Focused("upFromEnd", "option-0-1");
            // Enter on an answer picks it and goes on to Next; Enter there goes to the next step, the keyboard on its first answer.
            await Enter();
            await Check("enterPicks", "document.querySelector('[data-answer=option-0-1]')?.getAttribute('aria-checked') === 'true' && document.querySelector('[data-answer=option-0-0]')?.getAttribute('aria-checked') === 'false'");
            await Focused("enterGoesForward", "next");
            await Enter(); await Focused("enterNext", "option-1-0");
            // A press on the question's words puts the keyboard on the card: its first answer here.
            await Eval("document.querySelector('[data-testid=composer-input]')?.focus()");
            await Mouse("[data-card=questionnaire] .question-text");
            await Focused("mouseOnWords", "option-1-0");
            // Several answers: Space and Enter both toggle and stay; End then Enter sends.
            await Space(); await Check("spaceToggles", "document.querySelector('[data-answer=option-1-0]')?.getAttribute('aria-checked') === 'true'");
            await Focused("spaceStays", "option-1-0");
            await Down(); await Enter(); await Check("enterToggles", "document.querySelector('[data-answer=option-1-1]')?.getAttribute('aria-checked') === 'true'");
            await Focused("enterStays", "option-1-1");
            await End(); await Focused("endSubmit", "submit");
            await Enter();
            var answered = await Until("!document.querySelector('[data-card=questionnaire]')", 5) && responses.ContainsKey("web-keys-question");
            steps.Add(new { step = "enterSubmits", passed = answered, picked = view.WebSmokeAnswer }); ok &= answered && view.WebSmokeAnswer == "Blue";

            // A permission: a press on its title puts the keyboard on its first button; the arrows, then Enter allows.
            view.ReceiveToolPermission(new ToolPermissionRequest("web-keys-perm", paneId, "web-keys-tuid-2", "Bash", "{\"command\":\"ls /tmp\"}", "Run command"));
            ok &= await Until("!!document.querySelector('[data-card=permission] [data-answer=allow]')", 15);
            await Mouse("[data-card=permission] .card-title");
            await Focused("permissionMouseOnWords", "deny");
            await Right(); await Focused("permissionRight", "allow");
            await Left(); await Focused("permissionLeft", "deny");
            await End(); await Enter();
            var allowed = await Until("!document.querySelector('[data-card=permission]')", 5) && responses.TryGetValue("web-keys-perm", out var allow) && allow;
            steps.Add(new { step = "permissionEnterAllows", passed = allowed }); ok &= allowed;

            // A plan arrives while the composer has the keyboard; a press on the plan's words moves it to the card.
            await Eval("document.querySelector('[data-testid=composer-input]')?.focus()");
            view.ReceiveToolPermission(new ToolPermissionRequest("web-keys-plan", paneId, "toolu_web-keys-plan", ClaudePlanMode.ToolName,
                "{\"plan\":\"# Smoke plan\\n\\n1. First step\\n2. Second step\"}", "plan", CanAllow: false, CanAnswerPlan: true, ReceivedAt: "2027-01-15T08:05:00.000Z"));
            ok &= await Until("!!document.querySelector('[data-card=plan] [data-answer=plan-cancel]')", 15);
            await Check("planArrivesComposerKept", Composer);
            await Mouse("[data-card=plan] [data-testid=plan-text]");
            await Focused("planMouseOnWords", "plan-expand");
            await End(); await Focused("planEnd", "plan-approveAuto");
            await Home(); await Right(); await Right(); await Focused("planRight", "plan-revise-open");
            // Enter opens the change request; Enter in its box is a new line, Tab leaves it as usual, Enter on Send sends.
            await Enter(); await Check("planReviseOpens", "document.activeElement?.matches('[data-card=plan] .plan-revise textarea') === true");
            await core.CallDevToolsProtocolMethodAsync("Input.insertText", "{\"text\":\"fix\"}");
            await Enter(); await Down();
            await core.CallDevToolsProtocolMethodAsync("Input.insertText", "{\"text\":\"it\"}");
            await Check("planEnterNewLine", "document.activeElement?.tagName === 'TEXTAREA' && document.activeElement.value === 'fix\\nit' && !!document.querySelector('[data-card=plan]')");
            await Tab(); await Focused("planTab", "plan-revise-close");
            await Tab(); await Focused("planTabSend", "plan-revise-send");
            await Enter();
            var sent = await Until("!document.querySelector('[data-card=plan]')", 5) && plans is [{ Kind: "revise", Feedback: "fix\nit" }];
            steps.Add(new { step = "planEnterSends", passed = sent, decisions = plans.Select(p => p.Kind + ":" + p.Feedback).ToArray() }); ok &= sent;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { steps.Add(new { step = "error", passed = false, error = ex.Message }); ok = false; }
        finally { smokePlanAnswerer = previousPlan; }
        result["cardsKeys"] = new { passed = ok, steps };
        return ok;
    }

    private sealed partial class PaneView
    {
        /// <summary>What the status line last drew (RenderStatusLine), for the page.</summary>
        private (StatusLineConfig? Config, StatusLineConfig? Untrusted, StatusLineResult? Result, int Padding) webStatusLine;
        /// <summary>The smoke's questionnaire answer as it reached the host (the first question's).</summary>
        internal string? WebSmokeAnswer { get; private set; }

        /// <summary>The cards in the composer's order (MainWindow.Composer.cs: next actions, plan history, background work, the permission host, web-open prompts, then the sign-in card and the status line inside the composer).</summary>
        internal IReadOnlyList<object> WebCards(out bool ticks)
        {
            var pane = Session; var cards = new List<object>(); ticks = false;
            if (WebNextActions(pane) is { } next) cards.Add(next);
            if (WebPlanHistory(pane) is { } history) cards.Add(history);
            if (WebBackground(pane) is { } background) { cards.Add(background); ticks = background.Open && pane.BackgroundWork!.Tasks.Any(t => t.Status == "running"); }
            if (WebRequestCard() is { } request) cards.Add(request);
            foreach (var prompt in owner.agentUrlPrompts.ForPane(id)) cards.Add(new WebOpenCard(prompt.Id, prompt.Url.AbsoluteUri));
            if (WebLogin() is { } login) cards.Add(login);
            if (WebStatusLine() is { } line) cards.Add(line);
            return cards;
        }

        /// <summary>As <see cref="RefreshNextActions"/>: an idle agent pane whose latest reply ends with options.</summary>
        private WebNextActionsCard? WebNextActions(RunSession pane)
        {
            var busy = pane.Status == "running" || starting || queueStarting || owner.BackgroundUpdateHolds(pane);
            if (pane.Kind != "claude" || busy) return null;
            var reply = pane.Logs.LastOrDefault(entry => entry.Kind is "user" or "assistant");
            if (reply?.Kind != "assistant") return null;
            var actions = NextActions.Parse(reply.Text);
            return actions.Count == 0 ? null : new(reply.Id, actions.Select(a => a.DisplayLabel).ToArray());
        }

        /// <summary>A next action's words into the draft the page mirrors here (<see cref="FillNextAction"/>'s checks and insertion): the draft after it, or null when it may not.</summary>
        internal string? WebNextActionFill(string entryId, int index)
        {
            if (!QueuePaneAlive || owner.dialogOpen || starting || queueStarting || Session.Status == "running" || owner.service.IsSessionRunning(id) || owner.BackgroundUpdateHolds(Session)) return null;
            if (NextActions.Latest(Session.Logs) is not { } latest || latest.EntryId != entryId || index < 0 || index >= latest.Actions.Count) return null;
            var draft = input.Text; var insertion = NextActions.Insertion(draft, latest.Actions[index].Fill);
            return insertion.ReplacesDraft ? insertion.Text : draft + insertion.Text;
        }

        /// <summary>As <see cref="RefreshPlanHistory"/>: the pane's answered plans, newest first.</summary>
        private WebPlanHistoryCard? WebPlanHistory(RunSession pane)
        {
            var records = pane.Kind == "claude" && PlanCardSupport.ShowsHistoryStrip(InMightyDiagram) ? pane.PlanHistory ?? [] : [];
            if (records.Count == 0) return null;
            return new(Enumerable.Reverse(records).Select(r => new WebPlanRecord(r.Id, PlanCardSupport.OutcomeTitle(r.Outcome),
                r.Outcome switch { PlanOutcome.ApprovedAuto or PlanOutcome.ApprovedConfirm => "done", PlanOutcome.Revised => "wait", _ => "stop" },
                PlanCardSupport.TimeText(r.DecidedAt), PlanCardSupport.Headline(r.Plan), r.Plan, r.PlanTruncated == true, r.Feedback is { Length: > 0 } f ? f : null)).ToArray());
        }

        /// <summary>As <see cref="RefreshBackgroundWork"/>: the strip's line and, open, its rows.</summary>
        private WebBackgroundCard? WebBackground(RunSession pane)
        {
            var mighty = pane.Kind == "claude" && pane.Provider == "claude" && pane.AgentViewMode == "mighty";
            var drawsTasks = mighty && activeStyle?.Evaluator.DrawsTasks == true;
            var work = pane.BackgroundWork;
            if (pane.Kind != "claude" || !PlanCardSupport.ShowsBackgroundStrip(work, mighty, drawsTasks, owner.ShowsBackgroundWork)) return null;
            var open = owner.BackgroundWorkOpen; var now = DateTimeOffset.UtcNow;
            var tasks = open ? PlanCardSupport.BackgroundStripRows(work, pane.GraphRuns?.LastOrDefault(), owner.BackgroundOutputFor(pane.Id))
                .Select(row => new WebBackgroundTask(row.Task.Text, row.Task.Status, row.Detail(now), row.Tail)).ToArray() : [];
            return new(work!.Tasks.Count, PlanCardSupport.BackgroundStripSummary(work), open, tasks);
        }

        internal Task WebBackgroundWork(string action) => action switch
        {
            "toggle" => owner.SetBackgroundWorkOpen(!owner.BackgroundWorkOpen),
            "hide" => owner.SetShowsBackgroundWork(false),
            _ => throw new ArgumentException("Unknown action."),
        };

        /// <summary>The permission host's card as <see cref="RenderToolPermissionCards"/> picks it: the first request waiting, else the Antigravity card.</summary>
        private object? WebRequestCard()
        {
            var pending = CardRequests;
            if (pending.Count == 0)
            {
                if (owner.service.AntigravityCard(id) is not { } card) return null;
                var reason = owner.service.AntigravityError(id); var busy = owner.service.AntigravityAnswering(id, card);
                return new WebAntigravityCard(card.Id, card.RunId,
                    card.Actions.Select(a => new WebAntigravityAction(AntigravityPermissionFlow.KindLabel(a.Kind) + " · " + a.DisplayName, a.Target, a.Kind is "command" or "file", AntigravityPermissionFlow.RuleLine(a), AntigravityPermissionFlow.OnceOnlyText(a))).ToArray(),
                    reason, busy, card.CanAllow, card.CanAllowAlways,
                    card.CanAllow && !card.CanAllowAlways ? card.Actions.Select(AntigravityPermissionFlow.OnceOnlyText).OfType<string>().FirstOrDefault() ?? Locale.Get("antigravity.permission.onceOnly") : null);
            }
            var current = pending[0];
            if (current.CanAnswerPlan && current.Plan is not null)
            {
                var place = ComposerPlanPlace(current);
                if (place == PlanCardSupport.ComposerPlaces.PanelActions) return null;
                var draft = planDraft?.RequestId == current.Id ? planDraft : null;
                return new WebPlanCard(current.Id, pending.Count, current.ReceivedAt is { } at ? PlanCardSupport.ReceivedText(at) : "", current.Plan,
                    place == PlanCardSupport.ComposerPlaces.BarActions, draft?.Sending == true, draft?.Error);
            }
            if (current.CanAnswerQuestions && current.ToolName == "AskUserQuestion" && UserQuestionnaire.Parse(current.InputJson) is { } questionnaire)
                return new WebQuestionnaireCard(current.Id, pending.Count, questionnaire.Questions.Select(q => new WebQuestion(q.Header, q.Question, q.MultiSelect, q.Options.Select(o => new WebQuestionOption(o.Label, o.Description)).ToArray())).ToArray());
            var pres = ToolPermissionPresentation.Make(current.ToolName, current.InputJson);
            var bare = pres.Fields.Count == 0 && pres.Headline is null;
            return new WebPermissionCard(current.Id, ToolPermissionStrings.BarTitleTemplate.Replace("{title}", pres.Title), current.ToolName,
                pending.Count > 1 ? ToolPermissionStrings.BarWaitingCountTemplate.Replace("{count}", pending.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)) : null,
                pres.Headline, pres.Fields.Select(f => new WebPermissionField(f.Label, f.Value, f.Code)).ToArray(), bare ? current.Summary : null,
                current.BlockedPath is { Length: > 0 } path ? ToolPermissionStrings.BarPathTemplate.Replace("{path}", path) : null,
                current.Reason is { Length: > 0 } reasonText ? reasonText : null, current.CanAllow ? null : ToolPermissionStrings.BarCannotAllowHere, current.CanAllow,
                current.InputJson, ToolPermissionStrings.BarRawJson, ToolPermissionStrings.BarOnceOnlyNote, ToolPermissionStrings.ButtonDeny, ToolPermissionStrings.ButtonAllowOnce);
        }

        /// <summary>Allow once or Deny, for the request the card showed (<see cref="OnPermissionAllow"/>, <see cref="OnPermissionDeny"/>, which answer the first waiting).</summary>
        internal void WebAnswerPermission(string requestId, bool allow)
        {
            if (CardRequests.FirstOrDefault()?.Id != requestId) throw new InvalidOperationException(Locale.Get("remote.error.notPendingPermission"));
            if (allow) OnPermissionAllow(); else OnPermissionDeny();
        }

        /// <summary>
        /// The page's answers to the question card, one per question in order: read into the card's draft and sent as its Send
        /// does (<see cref="QuestionnaireAnswers"/>, <see cref="UserQuestionnaire.ValidateAnswers"/>, AnswerQuestionnaire). The refusal's words, or null.
        /// </summary>
        internal string? WebAnswerQuestionnaire(string requestId, IReadOnlyList<(IReadOnlyList<string> Picks, string? Custom)> answers)
        {
            var current = CardRequests.FirstOrDefault();
            if (current?.Id != requestId || UserQuestionnaire.Parse(current.InputJson) is not { } questionnaire) return Locale.Get("remote.error.notPendingPermission");
            var draft = new QuestionDraft(current.InputJson);
            for (var index = 0; index < answers.Count; index++)
            {
                draft.Picks[index] = answers[index].Picks.ToHashSet(StringComparer.Ordinal);
                if (answers[index].Custom is { } custom) { draft.Custom.Add(index); draft.Text[index] = custom; }
            }
            try
            {
                var picked = QuestionnaireAnswers(questionnaire, draft); var said = questionnaire.ValidateAnswers(picked);
                if (owner.smokePermissionResponses is { } smoke) { smoke[current.Id] = true; WebSmokeAnswer = said.Values.FirstOrDefault(); }
                else owner.service.AnswerQuestionnaire(id, current.Id, picked);
                ReceiveToolPermission(current with { State = "answered" });
                return null;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return ex.Message; }
        }

        /// <summary>One of the plan card's four answers through <see cref="AnswerPlanCard"/>; its error and sending state come back with the next push.</summary>
        internal Task WebAnswerPlan(string requestId, string decision, string? feedback)
        {
            if (CardRequests.FirstOrDefault() is not { } request || request.Id != requestId || !request.CanAnswerPlan) throw new InvalidOperationException(Locale.Get("remote.error.notPendingPermission"));
            var answer = decision switch
            {
                "cancel" => PlanDecision.Cancel,
                "revise" => PlanDecision.Revise(feedback ?? ""),
                "approveConfirm" => PlanDecision.ApproveConfirmEach,
                "approveAuto" => PlanDecision.ApproveAutoEdit,
                _ => throw new ArgumentException("Unknown decision."),
            };
            return AnswerPlanCard(request, answer);
        }

        /// <summary>The Antigravity card's answer through <see cref="AnswerAntigravityPermission"/> (its continuation queued behind a busy pane); why nothing happened, or null.</summary>
        internal async Task<string?> WebAnswerAntigravity(string cardId, AntigravityPermissionDecision decision)
        {
            if (owner.service.AntigravityCard(id) is not { } card || card.Id != cardId) return Locale.Get("remote.error.notPendingPermission");
            try { return await AnswerAntigravityPermission(card, decision); }
            finally { RenderAntigravityCard(); }
        }

        /// <summary>The sign-in card as <see cref="RenderLoginRecovery"/> reads it.</summary>
        private WebLoginCard? WebLogin()
        {
            if (!owner.loginRetries.Requests.TryGetValue(id, out var retry)) return null;
            var provider = retry.Request.Provider;
            var job = owner.loginJobs.GetValueOrDefault(provider); var progress = job?.Process?.Output;
            var failure = owner.loginFailures.GetValueOrDefault(provider); var note = owner.loginNotes.GetValueOrDefault(id);
            var pending = job is null && failure is null && owner.accountLoginWaits.ContainsKey(provider);
            var lines = new List<WebLoginLine>();
            if (note is not null) lines.Add(new(Locale.Get("loginRecovery.resendBlockedTemplate", new Dictionary<string, string> { ["reason"] = note }), "wait", false));
            if (job is not null)
            {
                if (job.Automatic) lines.Add(new(Locale.Get("loginRecovery.autoStarted"), "ink2", false));
                lines.Add(new(Locale.Get(job.Phase == "starting" ? "loginRecovery.starting" : job.Terminal ? "loginRecovery.antigravityTerminal" : "loginRecovery.waiting"), "ink2", true));
            }
            else if (failure is not null) lines.Add(new(failure, "wait", false));
            else lines.Add(new(Locale.Get(pending ? "loginRecovery.terminalPending" : "loginRecovery.body"), "ink2", false));
            var buttons = job is not null
                ? provider != "gemini" ? new[] { "cancel", "terminal" } : ["cancel"]
                : failure is not null && provider != "gemini" ? ["login", "terminal", "resend"] : ["login", "resend"];
            return new(Locale.Get("loginRecovery.title", new Dictionary<string, string> { ["provider"] = ProviderCatalog.Name(provider) }), lines, progress?.Url?.AbsoluteUri, progress?.AsksForCode == true, buttons, !pending);
        }

        /// <summary>The sign-in card's buttons, each the WinUI card's own call.</summary>
        internal Task WebLoginAction(string action, string? code)
        {
            if (!owner.loginRetries.Requests.TryGetValue(id, out var retry)) return Task.CompletedTask;
            var provider = retry.Request.Provider; var job = owner.loginJobs.GetValueOrDefault(provider);
            switch (action)
            {
                case "login": return owner.StartBackgroundLogin(provider, session: id);
                case "cancel": owner.CancelBackgroundLogin(provider); owner.RefreshLoginCards(); return Task.CompletedTask;
                case "terminal": return owner.TerminalLoginFallback(provider);
                case "resend": return owner.ResendLoginRequest(id);
                case "dismiss": owner.DismissLoginRecovery(id); return Task.CompletedTask;
                case "openLink":
                    if (job?.Process?.Output?.Url is { } url) Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
                    return Task.CompletedTask;
                case "code": job?.Process?.SendCode(code ?? ""); return Task.CompletedTask;
                default: throw new ArgumentException("Unknown action.");
            }
        }

        /// <summary>The status line as <see cref="RenderStatusLine"/> last drew it: the trust question, then at most six coloured rows or the failure.</summary>
        private WebStatusLineCard? WebStatusLine()
        {
            var (config, untrusted, result, _) = webStatusLine;
            var trust = untrusted is null ? null : new WebStatusTrust(StatusLineStrings.TrustPromptTemplate.Replace("{source}", untrusted.SourceLabel), untrusted.Command, StatusLineStrings.TrustAllow, StatusLineStrings.TrustDeny, StatusLineStrings.TrustNote);
            var lines = result is not null && config is not null ? result.Lines.Take(StatusLineSupport.MaximumLines).Select(line => (IReadOnlyList<WebStatusSegment>)line.Select(WebSegment).ToArray()).ToArray() : [];
            var error = result is not null && config is not null && result.Lines.Count == 0 && result.ErrorText is { Length: > 0 } failed ? failed : null;
            if (trust is null && lines.Length == 0 && error is null) return null;
            return new(lines, error, trust, config is null ? "statusLine" : "statusLine · " + config.SourceLabel + " · " + config.Command, StatusLineStrings.AccessibilityLabel);
        }

        /// <summary>One run's colour as <see cref="Row"/> picks it: the terminal's RGB, else the secondary ink for bright black, else the pane's ink.</summary>
        private static WebStatusSegment WebSegment(AnsiSegment segment)
        {
            var color = Terminal(segment.Foreground) is { } c ? $"#{c.R:x2}{c.G:x2}{c.B:x2}" : null;
            var ink = color is not null ? null : segment.Foreground is { Kind: AnsiColorKind.Standard, A: 8 } ? "ink2" : "ink";
            return new(segment.Text, color, ink, segment.Dim, segment.Bold, segment.Italic, segment.Underline);
        }

        /// <summary>The trust question's answers, as its two buttons: trust the workspace's command, or not now.</summary>
        internal async Task WebStatusLineTrust(bool allow)
        {
            var (config, untrusted, result, padding) = webStatusLine;
            if (untrusted is null) return;
            if (allow) await TrustStatusLine(untrusted);
            else { DismissStatusLine(); RenderStatusLine(config, null, result, padding); }
        }
    }
}
