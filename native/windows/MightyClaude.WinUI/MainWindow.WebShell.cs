using System.Diagnostics;
using System.Text.Json;
using MightyClaude.Core;
using Microsoft.UI.Xaml.Controls;

namespace MightyClaude.WinUI;

/// <summary>
/// The window's side of the web shell (WebShell.cs, docs: native/windows/web/README.md): while the state folder holds
/// <c>ui-web</c> the page stands where the WinUI screens stood, and the methods it calls are this window's own actions,
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
        RegisterConversationMethods(bridge); RegisterComposerMethods(bridge);
        webShell = new WebShell(bridge, StateDirectory, options.SmokeTest, brushes.Brush(DesignToken.Page).Color);
        root.KeyboardAccelerators.Clear(); root.Children.Clear();
        Grid.SetRowSpan(webShell, 3); Grid.SetColumnSpan(webShell, 2); root.Children.Add(webShell);
    }

    /// <summary>
    /// What the conversation asks of the host (native/windows/web/src/conversation). <c>openUrl</c> opens only http, https and
    /// mailto in the default browser, the way the transcript's and the login recovery's links open. <c>openReference</c> takes a
    /// file named in a pane the way the WinUI reference bubble resolves it (ReferencePreview.Resolve, inside the workspace, a
    /// remote pane's never) and, while the bubble is not drawn by the page, opens it in its default app (Windows' launcher, which
    /// refuses programs); the line is not carried there. <c>imageData</c> is a pane's picture by its hash, as a data URL the page's
    /// policy allows, or null.
    /// </summary>
    private void RegisterConversationMethods(WebBridge bridge)
    {
        bridge.Register("openUrl", args =>
        {
            if (Uri.TryCreate(args.Text("url"), UriKind.Absolute, out var url) && url.Scheme is "http" or "https" or "mailto")
                Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true })?.Dispose();
            return Task.FromResult<object?>(null);
        });
        bridge.Register("openReference", async args =>
        {
            if (args.Text("paneId") is not { } paneId || args.Text("path") is not { Length: > 0 and <= 4096 } path) return null;
            int? line = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("line", out var number) && number.ValueKind == JsonValueKind.Number && number.TryGetInt32(out var parsed) && parsed > 0 ? parsed : null;
            var snapshot = service.Snapshot;
            if (snapshot.Sessions.FirstOrDefault(s => s.Id == paneId) is not { } pane || LocalPath(snapshot.Workspaces.FirstOrDefault(w => w.Id == pane.WorkspaceId)) is not { } root) return null;
            var file = await Task.Run(() => ReferencePreview.Resolve(path, root, line) is { } target ? WorkspaceFiles.Resolve(target.RelativePath, root) : null);
            if (file is null || Directory.Exists(file)) return false;
            return await Windows.System.Launcher.LaunchFileAsync(await Windows.Storage.StorageFile.GetFileFromPathAsync(file));
        });
        bridge.Register("imageData", async args =>
        {
            var hash = args.Text("hash");
            if (hash is null || service.Snapshot.Sessions.FirstOrDefault(s => s.Id == args.Text("paneId")) is not { } pane) return null;
            var image = pane.Logs.SelectMany(entry => entry.Images ?? []).FirstOrDefault(i => i.Hash == hash);
            if (image is null || image.MediaType is not ("image/png" or "image/jpeg" or "image/gif" or "image/webp")) return null;
            var bytes = await Task.Run(() => service.Images.Data(image));
            return bytes is null ? null : "data:" + image.MediaType + ";base64," + Convert.ToBase64String(bytes);
        });
    }

    /// <summary>
    /// The smoke for the web shell, in the UI smoke's place when the smoke profile holds <c>ui-web</c>: three fixture panes go
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
            await service.UpdateAsync(state => state with { Sessions = sessions, ActiveWorkspaceId = workspace.Id, ActiveSessionId = sessions[0].Id });
            var shell = webShell ?? throw new InvalidOperationException("web shell missing");
            var core = await shell.Started ?? throw new InvalidOperationException("WebView2 did not start");
            const string Probe = "JSON.stringify({ mounted: !!document.querySelector('[data-testid=shell]'), titles: [...document.querySelectorAll('.sidebar-pane-title')].map(e => e.textContent), selected: document.querySelector('[data-testid=pane-title]')?.textContent ?? null, theme: document.documentElement.dataset.theme ?? null, "
                + "entries: document.querySelector('[data-testid=conversation]')?.dataset.entryCount ?? null, kinds: Object.fromEntries(['user', 'assistant', 'tool', 'output', 'system', 'error', 'image'].map(k => [k, document.querySelectorAll('[data-entry-kind=' + k + ']').length])), "
                + "tables: document.querySelectorAll('[data-md=table]').length, code: document.querySelectorAll('[data-code-block]').length, last: [...document.querySelectorAll('[data-entry-id]')].slice(-2).map(e => e.textContent) })";
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
            await core.ExecuteScriptAsync($"document.querySelector('[data-session-id=\"{sessions[1].Id}\"]')?.click()");
            var selected = await WaitFor(page => page.Text("selected") == titles[1]);
            result["afterClick"] = selected.Clone(); result["hostActiveSession"] = service.Snapshot.ActiveSessionId == sessions[1].Id;
            passed = shown && selected.Text("selected") == titles[1] && service.Snapshot.ActiveSessionId == sessions[1].Id;
            result["passed"] = passed;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { result["error"] = ex.GetType().Name + ": " + ex.Message; }
        Save();
        await FinishSmoke(passed);
    }
}
