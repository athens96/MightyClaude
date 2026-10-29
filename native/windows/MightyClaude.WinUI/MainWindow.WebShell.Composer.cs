using System.Text.Json;
using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MightyClaude.WinUI;

/// <summary>
/// The web page's composer (native/windows/web/src/composer) on the window's side. Each pane the page writes in has a WinUI
/// <see cref="PaneView"/> that is never shown, made the way the phone's are (<see cref="MobilePane"/>): its editor holds the
/// page's draft and its attachments, so a send, the queue, a stop and every block reason go through the same code the WinUI
/// composer runs (<see cref="PaneView.Submit"/>). When the view's composer state changes it is pushed to the page as a
/// <c>composer</c> message; the page asks for it once on mount with <c>composerState</c>.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>Panes whose composer state changed since the last push, and the JSON last pushed per pane.</summary>
    private readonly HashSet<string> webComposerDirty = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> webComposerSent = new(StringComparer.Ordinal);
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? webComposerFlush;

    /// <summary>The never-shown view of a pane the page writes in; a shell or agent pane, any provider.</summary>
    private PaneView WebPane(string? id)
    {
        if (id is null || !service.Snapshot.Sessions.Any(p => p.Id == id && p.Kind is "claude" or "shell")) throw new InvalidOperationException("Session not found.");
        if (!views.TryGetValue(id, out var pane)) { pane = new(this, id); views[id] = pane; pane.InitRefresher(); pane.Refresh(); }
        return pane;
    }

    /// <summary>Called at the end of every composer refresh (WinUI or never-shown view); a push follows within a frame or two.</summary>
    private void WebComposerChanged(PaneView view)
    {
        if (webShell is null) return;
        WebCardsChanged(view);
        webComposerDirty.Add(view.PaneId);
        if (webComposerFlush is null)
        {
            webComposerFlush = DispatcherQueue.CreateTimer(); webComposerFlush.Interval = TimeSpan.FromMilliseconds(30); webComposerFlush.IsRepeating = false;
            webComposerFlush.Tick += (_, _) => FlushWebComposers();
        }
        if (!webComposerFlush.IsRunning) webComposerFlush.Start();
    }

    private void FlushWebComposers()
    {
        if (webShell is null) return;
        foreach (var id in webComposerDirty.ToArray())
        {
            webComposerDirty.Remove(id);
            if (!views.TryGetValue(id, out var view) || !service.Snapshot.Sessions.Any(s => s.Id == id)) { webComposerSent.Remove(id); continue; }
            var state = view.WebComposerState(); var json = JsonSerializer.Serialize(state, Wire.Json);
            if (webComposerSent.TryGetValue(id, out var last) && last == json) continue;
            webComposerSent[id] = json; webShell.Bridge.Push(new { kind = "composer", state });
        }
    }

    private static bool Flag(JsonElement args, string name) => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>
    /// The composer's methods: the state, the draft, send / stop / the queue, and attachments (picked here, or dropped and pasted
    /// in the page as bytes). A refusal throws, and the page shows its message.
    /// </summary>
    private void RegisterComposerMethods(WebBridge bridge)
    {
        bridge.Register("composerState", args => Task.FromResult<object?>(WebPane(args.Text("paneId")).WebComposerState()));
        bridge.Register("updateDraft", args =>
        {
            WebPane(args.Text("paneId")).WebSetDraft(args.Text("text") ?? "", save: true);
            return Task.FromResult<object?>(null);
        });
        bridge.Register("send", async args =>
        {
            var view = WebPane(args.Text("paneId"));
            view.WebSetDraft(args.Text("text") ?? "", save: false);
            if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("attachments", out var ids) && ids.ValueKind == JsonValueKind.Array)
                view.WebKeepAttachments(ids.EnumerateArray().Select(i => i.GetString() ?? "").ToHashSet(StringComparer.Ordinal));
            // What the send did, its user entry and graph run when it started at once, and the page's own clientId back, so the
            // page matches its waiting request block to what the host delta brings (natural submit B5, SubmitReceipt.SendAnswer).
            var receipt = await view.Submit(Flag(args, "second"));
            return receipt.SendAnswer(args.Text("clientId"), view.WebDraft, view.WebComposerState());
        });
        bridge.Register("stop", async args => { await WebPane(args.Text("paneId")).WebStop(); return null; });
        bridge.Register("queueRemove", args => { WebPane(args.Text("paneId")).WebQueueRemove(args.Text("itemId") ?? ""); return Task.FromResult<object?>(null); });
        bridge.Register("queueRunNext", async args => { await WebPane(args.Text("paneId")).WebRunNext(); return null; });
        bridge.Register("installCli", args =>
        {
            if (service.Snapshot.Sessions.FirstOrDefault(s => s.Id == args.Text("paneId")) is { } pane && SetupItemForMissingCli(pane) is { } item) OpenSetup(item);
            return Task.FromResult<object?>(null);
        });
        bridge.Register("pickAttachments", async args => { await WebPane(args.Text("paneId")).WebPickAttachments(); return null; });
        bridge.Register("addAttachments", async args =>
        {
            var view = WebPane(args.Text("paneId"));
            var files = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("files", out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().Select(f => (Name: f.Text("name") ?? "attachment", Data: f.Text("data") ?? "")).ToList() : [];
            await view.WebAddAttachments(files);
            return null;
        });
        bridge.Register("removeAttachment", args => { WebPane(args.Text("paneId")).WebRemoveAttachment(args.Text("id") ?? ""); return Task.FromResult<object?>(null); });
        bridge.Register("dismissAttachmentError", args => { WebPane(args.Text("paneId")).WebDismissAttachmentError(); return Task.FromResult<object?>(null); });
        // The slash palette: the rows the WinUI palette lists (built-ins, the scanned catalogue, each argument's choices); the page filters them.
        bridge.Register("slashCommands", async args => await WebPane(args.Text("paneId")).WebSlashCatalog());
        bridge.Register("slashAction", async args =>
        {
            var view = WebPane(args.Text("paneId"));
            if (!Enum.TryParse<SlashCommandAction>(args.Text("action"), out var action)) throw new ArgumentException("Unknown action.");
            return await view.WebSlashAction(action, args.Text("arg"));
        });
        // The pills' menus are the WinUI composer's own MenuFlyouts, built by the same code and read row by row; choosing a row runs its action.
        bridge.Register("composerMenu", args => Task.FromResult<object?>(WebPane(args.Text("paneId")).WebMenu(args.Text("menu") ?? "")));
        bridge.Register("composerMenuChoose", async args =>
        {
            var path = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("path", out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().Select(i => i.TryGetInt32(out var n) ? n : -1).ToArray() : [];
            await WebPane(args.Text("paneId")).WebMenuChoose(args.Text("menu") ?? "", path);
            return null;
        });
        bridge.Register("runSettings", args => Task.FromResult<object?>(WebPane(args.Text("paneId")).WebRunSettings()));
        bridge.Register("applyRunSettings", ApplyWebRunSettings);
        bridge.Register("toggleFast", async args => { await WebPane(args.Text("paneId")).WebToggleFast(); return null; });
        // The composer's two eye toggles of a Claude pane (M/SessionPaneView.swift backgroundWorkToggle, statusLineToggle): app-wide preferences.
        bridge.Register("composerToggle", async args =>
        {
            if (args.Text("which") == "background") await Act(() => SetShowsBackgroundWork(!ShowsBackgroundWork));
            else if (args.Text("which") == "statusLine") await Act(() => SetStatusLineEnabled(!service.Snapshot.StatusLineEnabled));
            Render();
            return null;
        });
        bridge.Register("sessionInfo", args => Task.FromResult<object?>(WebPane(args.Text("paneId")).WebSessionInfo()));
        bridge.Register("newConversation", async args => await WebPane(args.Text("paneId")).ResetConversationAsync(CancellationToken.None));
        // The 보낼 대상 switch (MainWindow.AskTargets.cs): a target the pane offers, or null for Claude; and its fallback notice taken down.
        bridge.Register("setAskTarget", async args =>
        {
            var view = WebPane(args.Text("paneId"));
            if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("target", out var target) || target.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) throw new ArgumentException(Locale.Get("remote.error.badBody"));
            await SetAskTarget(view.PaneId, target.ValueKind == JsonValueKind.String ? target.GetString() : null);
            return null;
        });
        bridge.Register("dismissAskNotice", args => { DismissAskNotice(WebPane(args.Text("paneId")).PaneId); return Task.FromResult<object?>(null); });
        bridge.Register("attachmentPreview", args => Task.FromResult(WebPane(args.Text("paneId")).WebAttachmentPreview(args.Text("id") ?? "")));
    }

    private async Task<object?> ApplyWebRunSettings(JsonElement args) =>
        await WebPane(args.Text("paneId")).WebApplyRunSettings(Flag(args, "fastMode"), args.Text("webSearch") ?? "default", Flag(args, "networkAccess"), args.Text("maxTurns"), args.Text("maxBudgetUsd"));

    /// <summary>
    /// The composer's part of the web smoke: text typed into the page's editor must reach the pane's draft in the service, and
    /// Enter must come back with what the send did (<see cref="PaneView.Submit"/>). The start goes to a stand-in that records the
    /// request (smokeStart): no CLI runs and no AI request is made. A send the pane refuses (no CLI in the smoke profile) is a result too.
    /// </summary>
    private async Task<bool> RunWebComposerSmoke(Microsoft.Web.WebView2.Core.CoreWebView2 core, string paneId, Dictionary<string, object?> result)
    {
        const string Typed = "web smoke draft-marker";
        const string Type = "(() => { const e = document.querySelector('[data-testid=composer-input]'); if (!e) return false; e.focus(); "
            + "Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value').set.call(e, '" + Typed + "'); e.dispatchEvent(new Event('input', { bubbles: true })); return true; })()";
        const string Enter = "(() => { const e = document.querySelector('[data-testid=composer-input]'); e?.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', code: 'Enter', keyCode: 13, bubbles: true, cancelable: true })); return !!e; })()";
        const string Outcome = "JSON.stringify({ outcome: document.querySelector('[data-testid=composer]')?.dataset.lastOutcome ?? null, text: document.querySelector('[data-testid=composer-input]')?.value ?? null, primary: !!document.querySelector('[data-testid=composer-primary]') })";
        var started = new List<string>(); var previous = smokeStart;
        smokeStart = request => { started.Add(request.Input); return Task.CompletedTask; };
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline && await core.ExecuteScriptAsync("!!document.querySelector('[data-testid=composer-input]')") != "true") await Task.Delay(100);
            var typed = await core.ExecuteScriptAsync(Type) == "true";
            string? Draft() => service.Snapshot.Sessions.FirstOrDefault(s => s.Id == paneId)?.Draft;
            deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline && Draft() != Typed) await Task.Delay(100);
            var drafted = Draft() == Typed;
            result["composerTyped"] = typed; result["composerDraft"] = drafted;
            await core.ExecuteScriptAsync(Enter);
            JsonElement page = default; string? outcome = null;
            deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline)
            {
                page = JsonDocument.Parse(JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync(Outcome)) ?? "{}").RootElement.Clone();
                if ((outcome = page.Text("outcome")) is not null) break;
                await Task.Delay(100);
            }
            result["composerSend"] = page; result["composerStarts"] = started.ToArray();
            // A start must have reached the stand-in with the typed words; a refusal must leave them in the editor.
            var answered = outcome switch
            {
                "started" => started.Any(text => text.Contains(Typed, StringComparison.Ordinal)),
                "blocked" or "notStarted" or "queued" or "none" => started.Count == 0,
                _ => false,
            };
            var settings = await RunWebRunSettingsSmoke(core, paneId, result);
            return typed && drafted && page.TryGetProperty("primary", out var primary) && primary.GetBoolean() && answered && settings;
        }
        finally { smokeStart = previous; }
    }

    /// <summary>A live busy push locks an already open popover; a start racing Apply is refused by the host and shown in it.</summary>
    private async Task<bool> RunWebRunSettingsSmoke(Microsoft.Web.WebView2.Core.CoreWebView2 core, string paneId, Dictionary<string, object?> result)
    {
        if (webShell is null) return false;
        var bridge = webShell.Bridge; var view = WebPane(paneId);
        var before = service.Snapshot.Sessions.First(p => p.Id == paneId);
        var value = before.Settings.MaxTurns == 17 ? "18" : "17";
        var calls = 0; var raceOnApply = false; string? refusal = null;
        var shown = false; var held = false; var blocked = false; var unlocked = false; var draftKept = false; var ctrlEnterIgnored = false; var refusalShown = false; var refused = false;
        string? route = null, error = null; var menuChosen = false; var diagnostics = new List<object>();
        var scope = "document.querySelector(" + JsonSerializer.Serialize(".pane-slot:not(.hidden)[data-pane-id=\"" + paneId + "\"]") + ")";
        string Query(string selector) => scope + "?.querySelector(" + JsonSerializer.Serialize(selector) + ")";
        async Task Capture(string step)
        {
            var page = await core.ExecuteScriptAsync("(() => { const p = " + scope + ", bar = p?.querySelector('.composer-pills'), dialog = p?.querySelector('[data-testid=run-settings]'); return JSON.stringify({ "
                + "panePresent: !!p, paneId: p?.dataset.paneId ?? null, viewportWidth: innerWidth, viewportHeight: innerHeight, devicePixelRatio, paneWidth: p?.getBoundingClientRect().width ?? null, toolbarParentWidth: bar?.parentElement?.getBoundingClientRect().width ?? null, "
                + "pills: [...(bar?.querySelectorAll('[data-pill]') ?? [])].map(e => ({ name: e.dataset.pill, disabled: e.disabled })), menus: [...(p?.querySelectorAll('[data-menu]') ?? [])].map(e => ({ name: e.dataset.menu, rows: [...e.querySelectorAll('[role=menuitem], [role=menuitemradio]')].map(b => ({ text: b.textContent.trim().slice(0, 160), disabled: b.disabled })) })), "
                + "popoverShown: !!dialog, fields: [...(dialog?.querySelectorAll('input') ?? [])].map(e => ({ inputMode: e.inputMode, disabled: e.matches(':disabled') })), applyDisabled: dialog?.querySelector('[data-testid=run-settings-apply]')?.disabled ?? null, "
                + "alerts: [...(p?.querySelectorAll('.composer-pill-problem, [data-testid=run-settings] [role=alert]') ?? [])].map(e => e.textContent), active: document.activeElement?.dataset.testid ?? document.activeElement?.tagName ?? null }); })()");
            diagnostics.Add(new { step, hostStatus = service.Snapshot.Sessions.FirstOrDefault(p => p.Id == paneId)?.Status, page = JsonDocument.Parse(JsonSerializer.Deserialize<string>(page) ?? "{}").RootElement.Clone() });
        }
        async Task<bool> Until(string script)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline) { if (await core.ExecuteScriptAsync(script) == "true") return true; await Task.Delay(50); }
            return false;
        }
        async Task Running(bool running)
        {
            await service.UpdateAsync(state => state with { Sessions = state.Sessions.Select(p => p.Id == paneId ? p with { Status = running ? "running" : before.Status } : p).ToList() });
            view.Refresh();
        }
        bridge.Register("applyRunSettings", async args =>
        {
            calls++;
            // The request already left the page, but a queue/mobile start won before the host handled it.
            if (raceOnApply) await Running(true);
            var answer = await ApplyWebRunSettings(args); refusal = answer as string; return answer;
        });
        try
        {
            await Until("!!(" + Query("[data-pill=more]") + " || " + Query("[data-pill=options]") + ")");
            await Capture("beforeOpen");
            // Below 420 px Toolbar folds More into Options. Follow the visible UI instead of requiring a wide pane.
            route = JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("(() => { const p = " + scope + "; if (!p) return 'missing-pane'; for (const name of ['more', 'options']) { const b = p.querySelector('[data-pill=' + name + ']'); if (b && !b.disabled) { b.click(); return name; } } return 'unavailable'; })()"));
            if (route == "options")
            {
                var row = "[...(" + scope + "?.querySelectorAll('[data-menu=options] button[role=menuitem]') ?? [])].find(b => !b.disabled && b.textContent.trim() === " + JsonSerializer.Serialize(Locale.Get("composer.runSettings.more")) + ")";
                await Until("!!(" + row + ")");
                await Capture("optionsOpened");
                menuChosen = await core.ExecuteScriptAsync("(() => { const b = " + row + "; b?.click(); return !!b; })()") == "true";
            }
            shown = await Until("!!" + Query("[data-testid=run-settings] input[inputmode=numeric]"));
            await Capture("afterOpen");
            if (!shown) return false;
            await core.ExecuteScriptAsync("(() => { const e = " + Query("[data-testid=run-settings] input[inputmode=numeric]") + "; if (!e) return; e.focus(); Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(e, " + JsonSerializer.Serialize(value) + "); e.dispatchEvent(new Event('input', { bubbles: true })); })()");
            await Running(true);
            held = await Until(Query("[data-testid=run-settings] fieldset") + "?.disabled === true && " + Query("[data-testid=run-settings-apply]") + "?.disabled === true");
            await Capture("held");
            var enter = Query("[data-testid=run-settings] input[inputmode=numeric]") + "?.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', code: 'Enter', keyCode: 13, bubbles: true, cancelable: true }))";
            await core.ExecuteScriptAsync(enter);
            await core.ExecuteScriptAsync(Query("[data-testid=run-settings-apply]") + "?.click()");
            await Task.Delay(100);
            blocked = calls == 0 && service.Snapshot.Sessions.First(p => p.Id == paneId).Settings == before.Settings;
            await Running(false);
            unlocked = await Until(Query("[data-testid=run-settings] fieldset") + "?.disabled === false && " + Query("[data-testid=run-settings-apply]") + "?.disabled === false");
            draftKept = await core.ExecuteScriptAsync(Query("[data-testid=run-settings] input[inputmode=numeric]") + "?.value === " + JsonSerializer.Serialize(value)) == "true";
            await Capture("unlocked");
            // As the Mac's Apply (.defaultAction): only a plain Enter applies; Ctrl+Enter leaves the popover as it is.
            await core.ExecuteScriptAsync(enter.Replace("keyCode: 13,", "keyCode: 13, ctrlKey: true,", StringComparison.Ordinal));
            await Task.Delay(100);
            ctrlEnterIgnored = calls == 0 && await core.ExecuteScriptAsync("!!" + Query("[data-testid=run-settings]")) == "true";
            raceOnApply = true;
            await core.ExecuteScriptAsync(enter);
            refusalShown = await Until(Query("[data-testid=run-settings] [role=alert]") + "?.textContent === " + JsonSerializer.Serialize(Locale.Get("phone.session.settingsLocked")));
            refused = calls == 1 && refusal == Locale.Get("phone.session.settingsLocked") && service.Snapshot.Sessions.First(p => p.Id == paneId).Settings == before.Settings;
            await Capture("afterRacedApply");
            return shown && held && blocked && unlocked && draftKept && ctrlEnterIgnored && refusalShown && refused;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { error = ex.GetType().Name + ": " + ex.Message; return false; }
        finally
        {
            result["composerRunSettings"] = new { shown, held, blocked, unlocked, draftKept, ctrlEnterIgnored, refusalShown, refused, calls, route, menuChosen, diagnostics, error };
            bridge.Register("applyRunSettings", ApplyWebRunSettings);
            await core.ExecuteScriptAsync(Query("[data-testid=run-settings] .popover-footer .popover-button") + "?.click()");
            await core.ExecuteScriptAsync(Query("[data-menu=options]") + "?.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))");
            await service.UpdateAsync(state => state with { Sessions = state.Sessions.Select(p => p.Id == paneId ? p with { Status = before.Status, Settings = before.Settings } : p).ToList() });
            view.Refresh();
        }
    }

    private sealed partial class PaneView
    {
        internal string PaneId => id;
        internal string WebDraft => input.Text;
        /// <summary>The editor's placeholder and the blocked row's reason as the last composer refresh chose them.</summary>
        private string placeholderKey = "composer.placeholder.idle", blockedReason = "";

        /// <summary>The page's draft into the editor the send reads; <paramref name="save"/> as a keystroke saves it (DesktopService.UpdateDraft).</summary>
        internal void WebSetDraft(string text, bool save)
        {
            if (text.Length > input.MaxLength) text = text[..input.MaxLength];
            draftLoaded = true;
            if (input.Text != text) { updating = true; input.Text = text; updating = false; }
            if (save) owner.service.UpdateDraft(id, text);
            RefreshPalette(text); RefreshComposerState();
        }

        /// <summary>Only the attachments the page still shows go with a send.</summary>
        internal void WebKeepAttachments(HashSet<string> ids) { if (pendingAttachments.RemoveAll(a => !ids.Contains(a.Id)) > 0) { RefreshAttachments(); RefreshComposerState(); } }

        internal Task WebStop() => StopActiveRun();

        internal void WebQueueRemove(string itemId) { if (queuedInputs.Remove(itemId)) { RenderQueuedInputs(); RefreshComposerState(); } }

        internal Task WebRunNext() => RunNext();

        internal Task WebPickAttachments() => PickAttachments();

        /// <summary>Files dropped or pasted in the page, as base64: each read as the picker's (5 MB each, 8 MB in all, 8 files), then the composer's own checks.</summary>
        internal Task WebAddAttachments(IReadOnlyList<(string Name, string Data)> files) => LoadAttachments(() =>
        {
            if (Session.Kind == "shell") throw new ArgumentException(Locale.Get("wire.startRun.attachmentAiOnly"));
            if (files.Count > AttachmentSupport.MaximumCount) throw new ArgumentException(Locale.Get("composer.attachment.tooMany"));
            var read = new List<RunAttachment>(); long total = 0;
            foreach (var (name, data) in files)
            {
                if (data.Length > (AttachmentSupport.MaximumFileBytes + 2) / 3 * 4) throw new ArgumentException(Locale.Get("composer.attachment.fileTooLarge"));
                byte[] bytes;
                try { bytes = Convert.FromBase64String(data); } catch (FormatException) { throw new IOException(Locale.Get("composer.attachment.readIncomplete")); }
                if (bytes.Length > AttachmentSupport.MaximumFileBytes) throw new ArgumentException(Locale.Get("composer.attachment.fileTooLarge"));
                total += bytes.Length; if (total > AttachmentSupport.MaximumTotalBytes) throw new ArgumentException(Locale.Get("attachments.error.totalTooLarge"));
                read.Add(AttachmentSupport.Make(Path.GetFileName(name), bytes));
            }
            return Task.FromResult(read);
        });

        internal void WebRemoveAttachment(string attachmentId) { pendingAttachments.RemoveAll(a => a.Id == attachmentId); ShowAttachmentError(null); RefreshAttachments(); RefreshComposerState(); }

        internal void WebDismissAttachmentError() { ShowAttachmentError(null); RefreshComposerState(); }

        /// <summary>An attachment's preview as the chip's dialog shows it: a picture as a data URL, text up to 20,000 characters, else nothing.</summary>
        internal object? WebAttachmentPreview(string attachmentId)
        {
            if (pendingAttachments.FirstOrDefault(a => a.Id == attachmentId) is not { } file) return null;
            var size = AttachmentSupport.DecodedLength(file);
            if (file.MediaType is "image/png" or "image/jpeg" or "image/gif" or "image/webp") return new { name = file.Name, mediaType = file.MediaType, size, image = "data:" + file.MediaType + ";base64," + file.DataBase64 };
            if (file.MediaType == "text/plain") { var text = System.Text.Encoding.UTF8.GetString(AttachmentSupport.Decode(file)); return new { name = file.Name, mediaType = file.MediaType, size, text = text[..Math.Min(text.Length, 20000)] }; }
            return new { name = file.Name, mediaType = file.MediaType, size };
        }

        private static object WebSlashRow(SlashCommand c) => new { invocation = c.Invocation, description = SlashPalette.Description(c), source = c.Source, origin = c.Origin.ToString(), action = c.Action?.ToString(), argument = c.Argument?.ToString(), actionArg = c.ActionArg };

        /// <summary>The palette's rows for this pane, the catalogue scanned first when it is missing or older than 30 s (as <see cref="ScanSlashCommands"/>).</summary>
        internal async Task<object> WebSlashCatalog()
        {
            var pane = Session;
            if (pane.Kind == "shell") return new { builtins = Array.Empty<object>(), scanned = Array.Empty<object>(), choices = new Dictionary<string, object[]>() };
            var path = SlashWorkspacePath;
            if (owner.slashCatalogs.IsStale(pane.Provider, path))
            {
                try { var commands = await Task.Run(() => SlashCommandCatalog.Commands(pane.Provider, path)); if (!owner.closing) owner.SetSlashCatalog(pane.Provider, path, commands); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { /* A pane with no catalogue still shows its built-ins. */ }
            }
            var builtins = SlashPalette.Builtins(pane.Provider);
            var scanned = owner.slashCatalogs.Get(pane.Provider, path) ?? [];
            var choices = builtins.Where(b => b.Argument is not null).ToDictionary(b => b.Invocation, b => ArgumentChoices(b.Argument!.Value, b.Invocation).Select(WebSlashRow).ToArray());
            return new { builtins = builtins.Select(WebSlashRow), scanned = scanned.Select(WebSlashRow), choices };
        }

        /// <summary>A palette row's app action, run as the WinUI palette runs it (notes in the transcript included); the draft is cleared. The editor's text after it.</summary>
        internal async Task<string> WebSlashAction(SlashCommandAction action, string? argument)
        {
            if (await PerformSlashAction(action, argument)) { slashDismissedFor = null; RefreshPalette(""); await SetDraft(""); }
            return input.Text;
        }

        /// <summary>The menu a pill opens, built as the WinUI pill builds it; kept until the next is asked for, so a choice runs the row the page showed.</summary>
        private (string Name, MenuFlyout Menu)? webMenu;

        private MenuFlyout WebBuildMenu(string name)
        {
            var pane = Session; var catalog = PaneRuntime(pane.Provider)?.ModelCatalog ?? ProviderCatalog.Fallback(pane.Provider);
            var levels = ProviderCatalog.Efforts(pane.Provider, pane.Model, catalog, RegisteredModelsFor(pane.Provider, Workspace, owner.service.Snapshot));
            return name switch
            {
                "model" => ModelMenu(pane, catalog),
                "effort" => ChoiceMenu(EffortChoices(pane, levels)),
                "permission" => ChoiceMenu(PermissionChoices(pane, Capabilities)),
                "options" => OptionsMenu(pane, Capabilities, levels),
                "actions" => PaneActionsMenu(pane),
                // The style menu (M/GuidedPanel.swift MightyStylePicker): the one the WinUI chip opens, built when the pane read its styles.
                "style" => stylePicker.Flyout as MenuFlyout ?? new MenuFlyout { Items = { Choice("CLI", () => Task.CompletedTask, true) } },
                _ => throw new ArgumentException("Unknown menu."),
            };
        }

        /// <summary>A menu's rows: a header, a separator, a choice (checked or not), an action, or a submenu with its own rows.</summary>
        internal object WebMenu(string name)
        {
            var menu = WebBuildMenu(name); webMenu = (name, menu);
            static object Row(MenuFlyoutItemBase item) => item switch
            {
                MenuFlyoutSubItem sub => new { kind = "submenu", text = sub.Text, enabled = sub.IsEnabled, items = sub.Items.Select(Row).ToArray() },
                ToggleMenuFlyoutItem choice => new { kind = "choice", text = choice.Text, enabled = choice.IsEnabled, @checked = choice.IsChecked, help = ToolTipService.GetToolTip(choice) as string },
                MenuFlyoutItem { Tag: null } header => new { kind = "header", text = header.Text },
                // The options menu's last row opens the run settings, which the page draws itself.
                MenuFlyoutItem more when more.Text == Locale.Get("composer.runSettings.more") => new { kind = "runSettings", text = more.Text },
                MenuFlyoutItem action => new { kind = "action", text = action.Text, enabled = action.IsEnabled, help = ToolTipService.GetToolTip(action) as string },
                _ => new { kind = "separator" },
            };
            return menu.Items.Select(Row).ToArray();
        }

        /// <summary>Runs the row at <paramref name="path"/> (indices, a submenu's row after its own) of the menu last listed.</summary>
        internal async Task WebMenuChoose(string name, int[] path)
        {
            var menu = webMenu is { } kept && kept.Name == name ? kept.Menu : WebBuildMenu(name);
            IList<MenuFlyoutItemBase> items = menu.Items; MenuFlyoutItemBase? row = null;
            foreach (var index in path)
            {
                if (index < 0 || index >= items.Count) throw new ArgumentException("Unknown menu row.");
                row = items[index]; items = row is MenuFlyoutSubItem sub ? sub.Items : [];
            }
            webMenu = null;
            if (row is MenuFlyoutItem { IsEnabled: true, Tag: Func<Task> action }) await action();
            if (QueuePaneAlive) RefreshComposerState();
        }

        /// <summary>The pills as the toolbar shows them: the labels, what is on, which show (ArrangeComposer's rules), and the context ring.</summary>
        private object WebPills(RunSession pane)
        {
            var catalog = PaneRuntime(pane.Provider)?.ModelCatalog ?? ProviderCatalog.Fallback(pane.Provider);
            var capabilities = PaneRuntime(pane.Provider)?.Capabilities ?? ProviderCatalog.Capabilities(pane.Provider);
            var levels = ProviderCatalog.Efforts(pane.Provider, pane.Model, catalog, RegisteredModelsFor(pane.Provider, Workspace, owner.service.Snapshot));
            var selection = ModelLabel.Selection(pane, catalog);
            var selectedModel = catalog.Models.FirstOrDefault(m => m.Value == pane.Model);
            var usage = pane.SessionUsage?.Provider == pane.Provider ? pane.SessionUsage : null;
            return new
            {
                provider = pane.Provider, model = selection,
                modelTip = ProviderCatalog.BetaLabel(pane.Provider, ProviderMark.Label(pane.Provider)) + " · " + selection + (selectedModel?.Description is { Length: > 0 } description ? "\n" + description : ""),
                showsEffort = capabilities.Effort || pane.Settings.Effort != "default", effort = EffortLabel(pane.Settings.Effort), effortEnabled = levels.Length > 0 || pane.Settings.Effort != "default",
                effortTip = levels.Length == 0 ? Locale.Get("composer.effort.unknownLevels") : Locale.Get("composer.effort.label") + " · " + Locale.Get("settings.run.appliesNextRequest"),
                permission = PermissionLabel(pane.Provider, pane.Settings.PermissionMode), permissionHelp = PermissionHelp(pane.Provider, pane.Settings.PermissionMode), unlocked = pane.Settings.PermissionMode == "fullAccess",
                showsFast = pane.Provider == "codex" && (capabilities.FastMode || pane.Settings.FastMode), fast = pane.Settings.FastMode, fastEnabled = Capabilities.FastMode || pane.Settings.FastMode,
                moreOn = pane.Settings.WebSearch != "default" || pane.Settings.NetworkAccess || pane.Settings.MaxTurns is not null || pane.Settings.MaxBudgetUsd is not null,
                contextPercent = usage?.ContextPercent is { } percent && double.IsFinite(percent) ? Math.Round(percent) : (double?)null,
                resuming = pane.ResumeId is not null,
            };
        }

        /// <summary>Codex Fast, as its pill toggles it.</summary>
        internal Task WebToggleFast() => updating ? Task.CompletedTask : ChangeSettings(s => s with { FastMode = !s.FastMode && Capabilities.FastMode });

        /// <summary>What the run settings popover shows (MainWindow.RunSettings.cs) and the values it starts from.</summary>
        internal object WebRunSettings()
        {
            var pane = Session; var caps = Capabilities; var catalog = PaneRuntime(pane.Provider)?.ModelCatalog ?? ProviderCatalog.Fallback(pane.Provider);
            var selected = catalog.Models.FirstOrDefault(m => m.Value == pane.Model); var mode = pane.Settings.PermissionMode;
            var allowed = mode is "acceptEdits" or "onRequest";
            return new
            {
                provider = ProviderMark.Label(pane.Provider), beta = ProviderCatalog.IsBeta(pane.Provider),
                model = ModelLabel.Selection(pane, catalog), modelDescription = selected?.Description is { Length: > 0 } d ? d : null,
                modelSource = Locale.Get(catalog.Source == "cli" ? "settings.run.modelSourceCli" : "settings.run.modelSourceDefault"),
                permission = PermissionLabel(pane.Provider, mode), permissionDescription = PermissionDescription(pane.Provider, mode),
                approval = mode == "fullAccess" ? null : Locale.Get(pane.Provider == "claude" || pane.Provider == "codex" && mode == "onRequest" ? "settings.run.approvalInApp" : "settings.run.approvalUnsupported"),
                fastSupported = caps.FastMode, webSearch = caps.WebSearch, networkAccess = caps.NetworkAccess, networkAllowed = allowed,
                networkNote = Locale.Get(mode == "fullAccess" ? "settings.run.shellNetworkFullAccess" : allowed ? "settings.run.shellNetworkAcceptEdits" : "settings.run.shellNetworkLocked"),
                maxTurns = caps.MaxTurns, maxBudgetUsd = caps.MaxBudgetUsd,
                values = new
                {
                    fastMode = pane.Settings.FastMode, webSearch = pane.Settings.WebSearch, networkAccess = pane.Settings.NetworkAccess,
                    maxTurns = pane.Settings.MaxTurns?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
                    maxBudgetUsd = pane.Settings.MaxBudgetUsd?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
                },
                searchDescriptions = new[] { "default", "disabled", "cached", "live" }.ToDictionary(v => v, SearchDescription),
                held = pane.Status == "running",
            };
        }

        /// <summary>Apply, validated as the popover's (<see cref="RunSettingsFrom"/>): the refusal's message, or null once saved.</summary>
        internal async Task<string?> WebApplyRunSettings(bool fastMode, string webSearch, bool networkAccess, string? turns, string? budget)
        {
            if (!QueuePaneAlive) return Locale.Get("remote.error.paneNotFound");
            // A web request may race a queue/mobile start before its composer push arrives. Refuse explicitly so Apply
            // keeps the draft open; null would tell the page that values which were ignored had been saved.
            if (starting || queueStarting || Session.Status == "running" || owner.service.IsSessionRunning(id) || owner.BackgroundUpdateHolds(Session))
                return Locale.Get("phone.session.settingsLocked");
            var (setting, refusal) = RunSettingsFrom(Session, fastMode, webSearch, networkAccess, turns, budget);
            if (setting is null) return refusal ?? "";
            await Change(p => p with { Settings = setting }); Refresh();
            return null;
        }

        /// <summary>The session popover's rows (MainWindow.SessionInfo.cs RefreshSessionInfo), labels and values in the language.</summary>
        internal object WebSessionInfo()
        {
            var pane = Session; var usage = pane.SessionUsage?.Provider == pane.Provider ? pane.SessionUsage : null;
            var workspace = owner.service.Snapshot.Workspaces.FirstOrDefault(w => w.Id == pane.WorkspaceId);
            var rows = new List<object>();
            void Row(string key, string label, string? value) { if (!string.IsNullOrEmpty(value)) rows.Add(new { key, label, value }); }
            var contextText = usage?.ContextUsedTokens is { } used && usage.ContextWindowTokens is { } limit
                ? Locale.Get("composer.sessionInfo.contextTokens", new Dictionary<string, string> { ["used"] = used.ToString("N0"), ["limit"] = limit.ToString("N0") })
                : usage?.ContextUsedTokens is { } usedOnly ? Locale.Get("composer.sessionInfo.contextUsedOnly", new Dictionary<string, string> { ["tokens"] = usedOnly.ToString("N0") })
                : usage?.ContextWindowTokens is { } limitOnly ? Locale.Get("composer.sessionInfo.contextLimitOnly", new Dictionary<string, string> { ["tokens"] = limitOnly.ToString("N0") })
                : Locale.Get("composer.sessionInfo.contextUnavailable");
            Row("model", Locale.Get(usage?.Model is null ? "composer.sessionInfo.selectedModel" : "composer.sessionInfo.usedModel"), usage?.Model is { } model ? ModelLabel.Text(model) : ModelLabel.Selection(pane, PaneRuntime(pane.Provider)?.ModelCatalog ?? ProviderCatalog.Fallback(pane.Provider)));
            Row("elapsed", Locale.Get("composer.sessionInfo.latestRequestTime"), pane.RunTiming is { IsValid: true } timing ? timing.Label() : null);
            Row("workspace", Locale.Get("composer.sessionInfo.workspace"), workspace?.Name);
            Row("host", Locale.Get("remote.sessionInfo.host"), workspace?.Remote?.HostName);
            Row("path", "", workspace?.Path);
            var tokens = usage is not null && (usage.InputTokens ?? usage.OutputTokens ?? usage.CacheReadTokens ?? usage.CacheWriteTokens ?? usage.ReasoningTokens ?? usage.TotalTokens) is not null;
            var tokenRows = new List<object>();
            void Token(string key, string label, long? value) { if (value is { } n) tokenRows.Add(new { key, label = Locale.Get(label), value = n.ToString("N0") }); }
            Token("input", "composer.sessionInfo.input", usage?.InputTokens); Token("output", "composer.sessionInfo.output", usage?.OutputTokens);
            Token("cache-read", "composer.sessionInfo.cacheRead", usage?.CacheReadTokens); Token("cache-write", "composer.sessionInfo.cacheWrite", usage?.CacheWriteTokens);
            Token("reasoning", "composer.sessionInfo.reasoning", usage?.ReasoningTokens); Token("total", "composer.sessionInfo.total", usage?.TotalTokens);
            var cost = usage?.CostUSD is { } spent && double.IsFinite(spent) && spent >= 0 ? spent : (double?)null;
            string? received = null;
            if (usage is not null && DateTimeOffset.TryParse(usage.UpdatedAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var at))
                received = Locale.Get("composer.sessionInfo.lastReceived", new Dictionary<string, string> { ["time"] = at.ToLocalTime().ToString("T", System.Globalization.CultureInfo.CurrentCulture) });
            return new
            {
                title = pane.Title, provider = pane.Provider, providerLabel = ProviderMark.Label(pane.Provider), beta = ProviderCatalog.IsBeta(pane.Provider),
                status = StateLabel(pane.Status), tone = StatusGlyph.Tone(pane.Status).ToString(),
                contextPercent = usage?.ContextPercent, context = contextText, rows,
                tokenScope = tokens ? Locale.Get("composer.sessionInfo.tokenScope", new Dictionary<string, string> { ["scope"] = UsageScope(usage!.TokenScope) }) : null, tokens = tokenRows,
                note = tokens && (usage!.CacheReadTokens ?? usage.CacheWriteTokens ?? usage.ReasoningTokens) is not null ? Locale.Get("composer.sessionInfo.note") : null,
                cost = cost is { } amount ? new { label = Locale.Get("composer.sessionInfo.costWithScope", new Dictionary<string, string> { ["scope"] = UsageScope(usage!.CostScope) }), value = $"${amount:0.####}" } : null,
                noUsage = usage is null || !tokens && usage.CostUSD is null && usage.ContextUsedTokens is null && usage.ContextWindowTokens is null,
                identifiers = new[] { (Label: Locale.Get("composer.sessionInfo.mightySessionId"), Value: pane.Id), (Label: Locale.Get("composer.sessionInfo.cliSessionId"), Value: usage?.ProviderSessionId ?? pane.ResumeId ?? "") }.Where(i => i.Value.Length > 0).Select(i => new { label = i.Label, value = i.Value }),
                reported = usage is null ? null : Locale.Get("composer.sessionInfo.reported", new Dictionary<string, string> { ["provider"] = ProviderMark.Label(pane.Provider) }), source = usage?.Source, received,
            };
        }

        /// <summary>
        /// The pane header's word and figures (spec section 1), as the WinUI header writes them: what waits on the user ("waiting 2"),
        /// else the turn's background agents while it runs, else the status word; then the figures after the clock and the tooltip
        /// (<see cref="HeaderFigures"/>). The clock itself ticks in the page from the pane's runTiming.
        /// </summary>
        private object WebHeader(RunSession pane)
        {
            var pending = PendingRequests; var shown = StatusGlyph.DisplayStatus(pane.Status, pending);
            var word = pending > 0 ? Locale.Get("phone.card.attention", new Dictionary<string, string> { ["count"] = pending.ToString(System.Globalization.CultureInfo.InvariantCulture) })
                : pane.Status == "running" && PlanCardSupport.BackgroundStatus(pane.BackgroundWork) is { } waiting ? waiting : StateLabel(shown);
            var (rest, _, help) = HeaderFigures(pane);
            return new { word, tone = StatusGlyph.Tone(shown).ToString().ToLowerInvariant(), timed = pane.Kind != "shell" && pane.RunTiming is not null, rest, help };
        }

        /// <summary>What the page draws of the composer, read from the state the last <see cref="RefreshComposerState"/> left.</summary>
        internal object WebComposerState()
        {
            var pane = Session;
            // The 보낼 대상 switch shows once the pane's catalogue says oh-my-claudecode's ask is there: a pane never scanned is scanned now.
            owner.RefreshAskCatalog(pane, whenStale: false);
            var askTargets = owner.AskTargetsFor(pane);
            var busy = pane.Status == "running" || starting || queueStarting || owner.BackgroundUpdateHolds(pane);
            var queueable = busy && !starting && !queueStarting && pane.Kind == "claude" && HasComposerContent;
            var primary = queueable ? "queue" : busy ? "stop" : "send";
            var held = owner.service.HeldItems(id);
            var mutationBlock = owner.ManualMutationBlockReason(pane);
            var queueEnabled = !starting && !queueStarting && !attachmentsLoading && !stopping && queuedInputs.Items.Count < QueuedInputBuffer.MaximumItems && mutationBlock is null;
            return new
            {
                paneId = id, kind = pane.Kind, busy, starting = starting || queueStarting, stopping, canSend, checkingModels, attachmentsLoading,
                placeholderKey, blocked = blockedReason.Length == 0 ? null : blockedReason,
                install = blockedReason.Length > 0 && mutationBlock is null && owner.SetupItemForMissingCli(pane) is not null,
                hold = owner.BackgroundUpdateHolds(pane) ? ProviderMark.Label(pane.Provider) : null,
                primary, primaryEnabled = primary == "queue" ? queueEnabled : busy ? !stopping : canSend,
                worktree = offersWorktree, worktreeMenuOnly, worktreeEnabled = offersWorktree && queueEnabled,
                queue = new
                {
                    held = held.Select(h => new { id = h.Id, text = h.Text }),
                    items = queuedInputs.Items.Select(q => new { id = q.Id, text = q.Text, attachments = q.Attachments.Select(a => a.Name) }),
                    runNext = DelegationQueueRows.OffersRunNext(busy, held.Count, SteersRunNext),
                    waitsOnBackground = BackgroundQueuePolicy.WaitsOnBackground(pane.BackgroundWork, queuedInputs.Items.Count)
                        ? owner.service.SecondSend(pane) == ComposerSecondSend.Worktree ? "queue.waitingOnBackground.windowsWorktree" : "queue.waitingOnBackground.windows" : null,
                },
                attachments = pendingAttachments.Select(a => new { id = a.Id, name = a.Name, mediaType = a.MediaType, size = AttachmentSupport.DecodedLength(a) }),
                attachmentError = attachmentErrorText.Text.Length == 0 ? null : attachmentErrorText.Text,
                attachmentsAllowed = pane.Kind != "shell",
                pills = pane.Kind == "shell" ? null : WebPills(pane),
                // The Mighty row over the editor (M/SessionPaneView.swift composer): the style chip with its source badge, and what the style is for.
                style = styleHost.Visibility == Visibility.Visible
                    ? new { label = stylePickerLabel.Text, badge = stylePickerBadge.Visibility == Visibility.Visible ? (stylePickerBadge.Child as TextBlock)?.Text : null, hint = styleHint.Text, enabled = stylePicker.IsEnabled }
                    : null,
                header = WebHeader(pane),
                // The marks at the toolbar's right (M/SessionPaneView.swift composerToolbar): the resume mark, and a Claude pane's
                // background-work and status-line toggles with whether each is on.
                resumed = pane.ResumeId is not null,
                toggles = pane.Kind == "claude" && pane.Provider == "claude"
                    ? new { background = owner.ShowsBackgroundWork, statusLine = LocalPath(Workspace) is null ? (bool?)null : owner.service.Snapshot.StatusLineEnabled }
                    : null,
                // The 보낼 대상 switch (M/SessionPaneView.swift askMenu): the targets offered (none hides it), the one sends go to now
                // (null for Claude), and the notice that a saved one is gone.
                askTargets = askTargets.Select(t => new { id = t.Id, label = t.Label }),
                askTarget = AskTargets.Effective(pane.AskTarget, askTargets)?.Id,
                askNotice = owner.askFallbackNotices.GetValueOrDefault(id),
            };
        }
    }
}
