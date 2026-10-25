using System.Diagnostics;
using System.Text.Json;
using MightyClaude.Core;
using Microsoft.UI.Xaml.Controls;

namespace MightyClaude.WinUI;

/// <summary>
/// The window's side of the web shell (WebShell.cs, docs: native/windows/web/README.md): unless the state folder holds
/// <c>ui-winui</c> the page stands where the WinUI screens stood, and the methods it calls are this window's own actions,
/// so a choice made in the page goes through the same state update a WinUI click does. Everything else the window starts
/// (the service, the companion, the phone link, notifications, updates) runs as before.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>The web front end in place of the WinUI screens; null unless <see cref="WebShell.Enabled"/>.</summary>
    private WebShell? webShell;

    /// <summary>
    /// Puts the page where the WinUI root's parts were. The root stays the window's content, holding only the page, so the
    /// dialogs and flyouts that still open from the window keep a XamlRoot; its key shortcuts go, since they act on screens
    /// no longer shown. <see cref="Render"/> then keeps the state work it does and builds no pane views.
    /// </summary>
    private void ShowWebShell()
    {
        var bridge = new WebBridge(service, DispatcherQueue);
        bridge.Register("selectSession", async args =>
        {
            if (args.Text("id") is { } id && Wire.Identifier(id)) await SelectLayoutSession(id);
            return null;
        });
        bridge.Register("setTheme", async args =>
        {
            var theme = args.Text("theme") == "light" ? "light" : "dark";
            await service.UpdateAsync(state => state with { Theme = theme }); Render();
            return null;
        });
        // The page holds every catalogue; the host knows what "system" reads on this computer (Locale.ChosenLanguage).
        bridge.Register("getLocale", _ => Task.FromResult<object?>(new { preference = service.Snapshot.LanguagePreference, language = Locale.ChosenLanguage() }));
        bridge.Register("log", args =>
        {
            var line = "web " + (args.Text("level") ?? "info") + ": " + Wire.Clean(args.Text("message"), 2048);
            PerfLog.Mark(line); options.TraceStartup(line);
            return Task.FromResult<object?>(null);
        });
        RegisterConversationMethods(bridge); RegisterComposerMethods(bridge); RegisterCardMethods(bridge); RegisterMightyMethods(bridge); RegisterShellMethods(bridge); RegisterPaneMethods(bridge); RegisterSettingsMethods(bridge);
        webShell = new WebShell(bridge, StateDirectory, options.SmokeTest, brushes.Brush(DesignToken.Page).Color);
        root.KeyboardAccelerators.Clear(); root.Children.Clear();
        // The forms the page draws stay in the tree under it, invisible (MainWindow.WebShell.Settings.cs).
        if (webNativeHost is { } forms) { Grid.SetRowSpan(forms, 3); Grid.SetColumnSpan(forms, 2); root.Children.Add(forms); }
        Grid.SetRowSpan(webShell, 3); Grid.SetColumnSpan(webShell, 2); root.Children.Add(webShell);
    }

    /// <summary>
    /// What the conversation asks of the host (native/windows/web/src/conversation). <c>openUrl</c> opens only http, https and
    /// mailto in the default browser, the way the transcript's and the login recovery's links open. The reference bubble's
    /// methods (src/pane/ReferenceBubble.tsx): <c>referencePreview</c> resolves a file named in a pane the way the WinUI bubble
    /// does (ReferencePreview.Resolve, inside the workspace, a remote pane's never) and reads it as the files pane does
    /// (<see cref="WebFilePreview"/>); <c>referenceOpen</c> (Windows' launcher, which refuses programs) and <c>referenceReveal</c>
    /// take the resolved path; <c>referenceSize</c> saves the bubble's size app-wide. <c>imageData</c> is a pane's picture by its hash, as a data URL the page's
    /// policy allows (an SVG drawn to PNG), or null; <c>imageAction</c> runs one of the picture viewer's buttons on it.
    /// </summary>
    private void RegisterConversationMethods(WebBridge bridge)
    {
        bridge.Register("openUrl", args =>
        {
            if (Uri.TryCreate(args.Text("url"), UriKind.Absolute, out var url) && url.Scheme is "http" or "https" or "mailto")
                Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true })?.Dispose();
            return Task.FromResult<object?>(null);
        });
        bridge.Register("referencePreview", async args =>
        {
            if (WebReferenceRoot(args) is not { } root || args.Text("path") is not { Length: > 0 and <= 4096 } path) return null;
            int? line = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("line", out var number) && number.ValueKind == JsonValueKind.Number && number.TryGetInt32(out var parsed) && parsed > 0 ? parsed : null;
            var target = await Task.Run(() => ReferencePreview.Resolve(path, root, line));
            if (target is null) return new { found = false };
            return new { found = true, relativePath = target.RelativePath, line = target.Line, preview = await WebFilePreview(root, target.RelativePath) };
        });
        // Resolved again at the click: the workspace or the file may have changed while the bubble was open.
        bridge.Register("referenceOpen", async args =>
        {
            if (WebReferenceRoot(args) is not { } root || WorkspaceFiles.Resolve(args.Text("path") ?? "", root) is not { } file || Directory.Exists(file)) return false;
            return await Windows.System.Launcher.LaunchFileAsync(await Windows.Storage.StorageFile.GetFileFromPathAsync(file));
        });
        bridge.Register("referenceReveal", args =>
        {
            if (WebReferenceRoot(args) is { } root && WorkspaceFiles.Resolve(args.Text("path") ?? "", root) is { } file)
            {
                try { using var _ = Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = "/select,\"" + file + "\"", UseShellExecute = false }); }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { error.Text = ex.Message; }
            }
            return Task.FromResult<object?>(null);
        });
        // The size a drag left, every pane's (none: 420 wide and as tall as the room), kept as ReferencePreview.BubbleSize keeps it.
        bridge.Register("referenceSize", async args =>
        {
            static double? Number(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var n) && n.ValueKind == JsonValueKind.Number ? n.GetDouble() : null;
            var (width, height) = ReferencePreview.BubbleSize(Number(args, "width"), Number(args, "height"));
            await service.UpdateAsync(state => state with { ReferenceBubbleWidth = width, ReferenceBubbleHeight = height });
            return null;
        });
        bridge.Register("imageData", async args =>
        {
            var hash = args.Text("hash");
            if (hash is null || service.Snapshot.Sessions.FirstOrDefault(s => s.Id == args.Text("paneId")) is not { } pane) return null;
            var image = pane.Logs.SelectMany(entry => entry.Images ?? []).FirstOrDefault(i => i.Hash == hash);
            return image is null ? null : await WebImageUrl(await Task.Run(() => service.Images.Data(image)), image.MediaType);
        });
        // The picture viewer's buttons (src/pictures/PictureViewer.tsx): the picture named as its thumbnail or tile was fetched,
        // a pane's log picture by its hash or a diagram step's by run, step and item id, read and checked again and handed to
        // the WinUI viewer's own actions (PaneView.TranscriptImageAction). Answers null when done, else the failure's words.
        bridge.Register("imageAction", async args =>
        {
            if (args.Text("action") is not ("copy" or "save" or "reveal" or "openDefault") || args.Text("paneId") is not { } paneId) return null;
            try
            {
                var view = WebPane(paneId);
                TranscriptAction? action = args.Text("hash") is { } hash
                    ? service.Snapshot.Sessions.First(s => s.Id == paneId).Logs.SelectMany(entry => entry.Images ?? []).FirstOrDefault(i => i.Hash == hash) is { } image ? new("image", image.Source, image) : null
                    : view.WebGalleryImage(args.Text("runId") ?? "", args.Text("step") ?? "", args.Text("id") ?? "");
                if (action is null) return Locale.Get("images.missing");
                await view.TranscriptImageAction(action, await view.LoadTranscriptImage(action), args.Text("action")!);
                return null;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return ex.Message; }
        });
    }

    /// <summary>Most a picture handed to the page may weigh, and the longest edge an SVG is drawn at for it.</summary>
    private const int WebPictureBytes = 8 * 1024 * 1024, WebSvgEdge = 2048;

    /// <summary>
    /// A picture's bytes as a data URL the page's policy allows: PNG, JPEG, GIF and WebP as they are, an SVG drawn to PNG no
    /// longer than <see cref="WebSvgEdge"/> (NativeSvgRaster, as the WinUI tile and viewer draw it); null for any other kind,
    /// more than <see cref="WebPictureBytes"/>, or an SVG that does not draw.
    /// </summary>
    private static async Task<string?> WebImageUrl(byte[]? data, string? type)
    {
        if (data is null || data.Length > WebPictureBytes) return null;
        if (type == "image/svg+xml" || AgentImageSupport.SvgDocument(data))
        {
            try { data = (await NativeSvgRaster.Render(data, WebSvgEdge)).Png; type = "image/png"; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
        }
        return data.Length > WebPictureBytes || type is not ("image/png" or "image/jpeg" or "image/gif" or "image/webp") ? null : "data:" + type + ";base64," + Convert.ToBase64String(data);
    }

    /// <summary>
    /// The reference bubble's web smoke: a fixture file under the smoke project, the reply's <c>src/a.ts:3</c> clicked where it is
    /// drawn (the caret under the point, as a person's click finds it), and the bubble must show the file's source with its line
    /// numbers, at line 3, then close from its close button.
    /// </summary>
    private static async Task<bool> RunWebReferenceSmoke(Microsoft.Web.WebView2.Core.CoreWebView2 core, string project, Dictionary<string, object?> result)
    {
        Directory.CreateDirectory(Path.Combine(project, "src"));
        await File.WriteAllTextAsync(Path.Combine(project, "src", "a.ts"), "// referenceMarker\nconst one = 1;\nconst three = 3;\nexport { one, three };\n");
        async Task<bool> True(string script) => await core.ExecuteScriptAsync(script) == "true";
        async Task<bool> Until(Func<Task<bool>> done)
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline) { if (await done()) return true; await Task.Delay(50); }
            return false;
        }
        const string Room = "(document.querySelector('.pane-slot.active:not(.hidden)') ?? document)";
        const string Click = "(() => { const c = " + Room + ".querySelector('[data-testid=conversation]'); if (!c) return false; const w = document.createTreeWalker(c, NodeFilter.SHOW_TEXT); let n; "
            + "while ((n = w.nextNode())) { const i = n.textContent.indexOf('src/a.ts:3'); if (i < 0) continue; n.parentElement.scrollIntoView({ block: 'center' }); const r = document.createRange(); r.setStart(n, i + 2); r.setEnd(n, i + 3); const b = r.getBoundingClientRect(); "
            + "n.parentElement.dispatchEvent(new MouseEvent('click', { bubbles: true, button: 0, clientX: b.left + b.width / 2, clientY: b.top + b.height / 2 })); return true; } return false; })()";
        const string Bubble = Room + ".querySelector('[data-testid=reference-bubble]')";
        var clicked = await True(Click);
        var opened = clicked && await Until(() => True("(() => { const b = " + Bubble + "; return !!b && b.dataset.referenceKind === 'source' && b.dataset.referenceLine === '3' && (b.querySelector('[data-testid=files-source]')?.textContent ?? '').includes('referenceMarker') && (b.querySelector('.files-lines')?.textContent ?? '').startsWith('1'); })()"));
        var closed = opened && await True("(() => { " + Bubble + "?.querySelector('[data-testid=reference-close]')?.click(); return true; })()") && await Until(() => True("!" + Bubble));
        result["referenceChecks"] = new { clicked, opened, closed };
        return opened && closed;
    }

    /// <summary>
    /// The picture viewer's web smoke (src/pictures/PictureViewer.tsx): an image entry holding a stored PNG and a stored SVG goes
    /// into the pane's log; both thumbnails must come as PNG data URLs (the SVG drawn by NativeSvgRaster), a click on the first
    /// must open the viewer as a dialog with focus inside it and the picture as a data URL, and Esc must close it and give focus
    /// back to the thumbnail. None of the viewer's buttons is pressed (copy would touch the clipboard). The entry goes again after.
    /// </summary>
    private async Task<bool> RunWebPictureSmoke(Microsoft.Web.WebView2.Core.CoreWebView2 core, string paneId, Dictionary<string, object?> result)
    {
        async Task<bool> True(string script) => await core.ExecuteScriptAsync(script) == "true";
        async Task<bool> Until(Func<Task<bool>> done)
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline) { if (await done()) return true; await Task.Delay(50); }
            return false;
        }
        var png = service.Images.Store(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAQAAAADCAIAAAA7ljmRAAAAEElEQVR4nGPQqDgBRww4OQBBxhDhzXmo9QAAAABJRU5ErkJggg=="), "image/png", "smoke-picture.png");
        var svg = service.Images.Store(System.Text.Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"30\"><rect width=\"40\" height=\"30\" fill=\"#c33\"/></svg>"), "image/svg+xml", "smoke-picture.svg");
        var entryId = Wire.Id();
        await service.UpdateAsync(state => state with { Sessions = state.Sessions.Select(s => s.Id != paneId ? s : s with { Logs = [.. s.Logs, new(entryId, "image", "Read · smoke-picture", Wire.Now(), Images: [png, svg])] }).ToList() });
        try
        {
            const string Room = "(document.querySelector('.pane-slot.active:not(.hidden)') ?? document)";
            const string Thumbs = "[..." + Room + ".querySelectorAll('[data-testid=thumbnail] img')]";
            const string Viewer = "document.querySelector('[data-testid=picture-viewer]')";
            var thumbnails = await Until(() => True("(() => { const t = " + Thumbs + "; return t.length === 2 && t.every(i => i.src.startsWith('data:image/png;base64,')); })()"));
            var clicked = thumbnails && await True("(() => { const b = " + Room + ".querySelector('[data-testid=thumbnail]'); if (!b) return false; b.scrollIntoView({ block: 'center' }); b.focus(); b.click(); return true; })()");
            var opened = clicked && await Until(() => True("(() => { const v = " + Viewer + "; const i = v?.querySelector('[data-testid=picture-viewer-image]'); return !!i && i.src.startsWith('data:image/') && !!v.querySelector('[role=dialog][aria-modal=true]') && v.contains(document.activeElement) && i.style.visibility !== 'hidden'; })()"));
            var closed = opened && await True("(() => { (document.activeElement ?? document.body).dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true })); return true; })()")
                && await Until(() => True("!" + Viewer + " && document.activeElement?.dataset.testid === 'thumbnail'"));
            result["pictureChecks"] = new { thumbnails, clicked, opened, closed };
            return opened && closed;
        }
        finally { await service.UpdateAsync(state => state with { Sessions = state.Sessions.Select(s => s.Id != paneId ? s : s with { Logs = s.Logs.Where(e => e.Id != entryId).ToList() }).ToList() }); }
    }

    /// <summary>The folder of the pane a reference names a file in; null for a remote pane (its files are on its host) or an unknown one.</summary>
    private string? WebReferenceRoot(JsonElement args)
    {
        var snapshot = service.Snapshot;
        return snapshot.Sessions.FirstOrDefault(s => s.Id == args.Text("paneId")) is { } pane ? LocalPath(snapshot.Workspaces.FirstOrDefault(w => w.Id == pane.WorkspaceId)) : null;
    }

    /// <summary>
    /// The smoke for the web shell, in the UI smoke's place when <c>MIGHTY_SMOKE_WEB=1</c> kept <c>ui-winui</c> out of the smoke profile: three fixture panes go
    /// into the state, and the page's DOM (read through ExecuteScriptAsync) must show the shell, every pane's title in the
    /// sidebar and the active pane in the main area; a click on the second row must make it the active pane in the host's
    /// state and in the page. The Claude pane holds one entry of each kind: the conversation must draw them (counted by their
    /// data attributes) and must show a streamed change, its last entry replaced and one appended through the service, as a
    /// delta. No AI request is made and nothing outside the smoke profile is touched.
    /// </summary>
    private async Task RunWebShellSmoke()
    {
        var directory = options.ProfileDirectory ?? throw new InvalidOperationException("smoke profile missing");
        var result = new Dictionary<string, object?> { ["passed"] = false, ["webShell"] = true, ["aiRequestSent"] = false, ["clipboardUsed"] = false };
        var passed = false;
        void Save() => File.WriteAllText(Path.Combine(directory, "smoke-result.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        try
        {
            var project = Path.Combine(directory, "Fixture project"); Directory.CreateDirectory(project);
            var workspace = await service.AddWorkspaceAsync(project);
            string[] titles = ["Web smoke Claude", "Web smoke Codex", "Web smoke shell"];
            // The Claude pane's conversation: a request, a finished and a failed tool call, a Markdown reply with a table and code, output and a note.
            List<LogEntry> claude =
            [
                new(Wire.Id(), "user", "fixture request 0", Wire.Now()),
                new(Wire.Id(), "assistant", "npm run build", Wire.Now(), "claude", new AgentActivity(Wire.Id(), "claude", "command", "completed", "npm run build", "Bash", "built", 1234)),
                new(Wire.Id(), "assistant", "src/missing.ts", Wire.Now(), "claude", new AgentActivity(Wire.Id(), "claude", "read", "error", "src/missing.ts", "Read")),
                new(Wire.Id(), "assistant", "fixture answer 0\n\n## Result\n\n**Passed**: see `src/a.ts:3`.\n\n| file | lines |\n|---|--:|\n| a.ts | 12 |\n\n```ts\nconst answer = 42;\n```", Wire.Now(), "claude"),
                new(Wire.Id(), "output", "PS> dir\n  a.txt", Wire.Now()),
                new(Wire.Id(), "system", "fixture note", Wire.Now()),
            ];
            var sessions = titles.Select((title, index) => new RunSession
            {
                WorkspaceId = workspace.Id, Title = title, Kind = index == 2 ? "shell" : "claude", Provider = index == 1 ? "codex" : "claude", Status = "completed",
                Logs = index == 0 ? claude : [new(Wire.Id(), "user", "fixture request " + index, Wire.Now()), new(Wire.Id(), "assistant", "fixture answer " + index, Wire.Now())],
            }).ToList();
            // One tab group, the workspace open in the sidebar: the frame's smoke clicks its rows and tabs (MainWindow.WebShell.Shell.cs).
            await service.UpdateAsync(state => state with { Sessions = sessions, ActiveWorkspaceId = workspace.Id, ActiveSessionId = sessions[0].Id, Layout = "tabs", ExpandedWorkspaceIds = [workspace.Id] });
            var shell = webShell ?? throw new InvalidOperationException("web shell missing");
            var core = await shell.Started ?? throw new InvalidOperationException("WebView2 did not start");
            // Counted inside the active pane slot: the page keeps panes it has shown mounted (hidden), so the document holds them all.
            const string Probe = "(() => { const R = document.querySelector('.pane-slot.active:not(.hidden)') ?? document; return JSON.stringify({ mounted: !!document.querySelector('[data-testid=shell]'), titles: [...document.querySelectorAll('.sidebar-pane-title')].map(e => e.textContent), selected: (document.querySelector('.pane-slot.active:not(.hidden) [data-testid=pane-title]') ?? document.querySelector('[data-testid=pane-title]'))?.textContent ?? null, theme: document.documentElement.dataset.theme ?? null, "
                + "entries: R.querySelector('[data-testid=conversation]')?.dataset.entryCount ?? null, kinds: Object.fromEntries(['user', 'assistant', 'tool', 'output', 'system', 'error', 'image'].map(k => [k, R.querySelectorAll('[data-entry-kind=' + k + ']').length])), "
                + "tables: R.querySelectorAll('[data-md=table]').length, code: R.querySelectorAll('[data-code-block]').length, last: [...R.querySelectorAll('[data-entry-id]')].slice(-2).map(e => e.textContent) }); })()";
            async Task<JsonElement> Read() => JsonDocument.Parse(JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync(Probe)) ?? "{}").RootElement;
            async Task<JsonElement> WaitFor(Func<JsonElement, bool> done)
            {
                var deadline = DateTime.UtcNow.AddSeconds(30); var last = default(JsonElement);
                while (DateTime.UtcNow < deadline) { last = await Read(); if (done(last)) return last; await Task.Delay(100); }
                return last;
            }
            static List<string> Titles(JsonElement page) => page.TryGetProperty("titles", out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().Select(t => t.GetString() ?? "").ToList() : [];
            var mounted = await WaitFor(page => page.TryGetProperty("mounted", out var m) && m.GetBoolean() && titles.All(Titles(page).Contains) && page.Text("selected") == titles[0]);
            result["bridgeReady"] = shell.Bridge.Ready; result["mounted"] = mounted.Clone(); Save();
            var shown = mounted.TryGetProperty("mounted", out var isMounted) && isMounted.GetBoolean() && titles.All(Titles(mounted).Contains) && mounted.Text("selected") == titles[0];
            static int Count(JsonElement page, string kind) => page.TryGetProperty("kinds", out var kinds) && kinds.ValueKind == JsonValueKind.Object && kinds.TryGetProperty(kind, out var n) && n.TryGetInt32(out var value) ? value : 0;
            static int Number(JsonElement page, string name) => page.TryGetProperty(name, out var n) && n.ValueKind == JsonValueKind.Number && n.TryGetInt32(out var value) ? value : 0;
            static List<string> Last(JsonElement page) => page.TryGetProperty("last", out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().Select(t => t.GetString() ?? "").ToList() : [];
            // Every kind drawn: the request, both tool calls, the reply with its table and its code, the output (a code block too) and the note.
            static bool Drawn(JsonElement page) => page.Text("entries") == "6" && Count(page, "user") == 1 && Count(page, "tool") == 2 && Count(page, "assistant") == 1
                && Count(page, "output") == 1 && Count(page, "system") == 1 && Number(page, "tables") == 1 && Number(page, "code") == 2;
            var drawn = await WaitFor(Drawn);
            result["conversation"] = drawn.Clone(); Save();
            // A streamed change: the last entry replaced and one appended, as a run's events do; the page gets them as a delta.
            await service.UpdateAsync(state => state with { Sessions = state.Sessions.Select(s => s.Id != sessions[0].Id ? s : s with { Logs = [.. s.Logs.SkipLast(1), s.Logs[^1] with { Text = "fixture note replaced-marker" }, new(Wire.Id(), "assistant", "appended-marker", Wire.Now(), "claude")] }).ToList() });
            static bool Streamed(JsonElement page) => page.Text("entries") == "7" && Last(page) is [var replaced, var appended] && replaced.Contains("replaced-marker", StringComparison.Ordinal) && appended.Contains("appended-marker", StringComparison.Ordinal);
            var streamed = await WaitFor(Streamed);
            result["streamed"] = streamed.Clone(); result["lastDeltaBytes"] = shell.Bridge.LastDeltaBytes; Save();
            shown = shown && Drawn(drawn) && Streamed(streamed);
            // The reference bubble: the reply's `src/a.ts:3` clicked opens the fixture file in the pane, at its line.
            var referenced = await RunWebReferenceSmoke(core, project, result); result["reference"] = referenced; Save();
            shown = shown && referenced;
            // The picture viewer: a PNG and an SVG picture in the conversation, the SVG drawn as PNG, the first opened and closed with Esc.
            var pictured = await RunWebPictureSmoke(core, sessions[0].Id, result); result["pictures"] = pictured; Save();
            shown = shown && pictured;
            // The composer: a typed draft reaches the service, and Enter answers with what the send did (MainWindow.WebShell.Composer.cs).
            var composed = await RunWebComposerSmoke(core, sessions[0].Id, result); result["composer"] = composed; Save();
            shown = shown && composed;
            // The cards: a fixture permission and a questionnaire drawn in the page and answered there (MainWindow.WebShell.Cards.cs).
            var carded = await RunWebCardsSmoke(core, sessions[0].Id, result); result["cards"] = carded; Save();
            shown = shown && carded;
            // The Mighty views: the diagram's blocks, edges and outline, a streamed change drawing one block, the log and the pipeline (MainWindow.WebShell.Mighty.cs).
            var mightied = await RunWebMightySmoke(core, sessions[0].Id, result); result["mighty"] = mightied; Save();
            shown = shown && mightied;
            // The frame: sidebar rows, a row click, timed tab clicks, the add-pane menu and a split preset (MainWindow.WebShell.Shell.cs).
            var framed = await RunWebShellFrameSmoke(core, sessions.Select(s => s.Id).ToList(), result); Save();
            shown = shown && framed;
            // The panes that are not a conversation: files, a terminal and a browser (MainWindow.WebShell.Panes.cs).
            var paned = await RunWebPanesSmoke(core, workspace, result); Save();
            shown = shown && paned;
            // Settings, the setup screen, the plugin browser and the dialogs drawn by the page (MainWindow.WebShell.Settings.cs).
            var set = await RunWebSettingsSmoke(core, workspace, sessions[2].Id, result); Save();
            shown = shown && set;
            await core.ExecuteScriptAsync($"document.querySelector('[data-session-id=\"{sessions[1].Id}\"]')?.click()");
            var selected = await WaitFor(page => page.Text("selected") == titles[1]);
            result["afterClick"] = selected.Clone(); result["hostActiveSession"] = service.Snapshot.ActiveSessionId == sessions[1].Id;
            // The live steps beside the page: settings forms, setup, plugins, remote, updates, delegation, companion, screen sharing (MainWindow.WebShell.LiveSmoke.cs).
            var lived = await RunWebLiveSmoke(workspace, result, Save);
            passed = shown && selected.Text("selected") == titles[1] && service.Snapshot.ActiveSessionId == sessions[1].Id && lived;
            result["passed"] = passed;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { result["error"] = ex.GetType().Name + ": " + ex.Message; }
        Save();
        await FinishSmoke(passed);
    }
}
