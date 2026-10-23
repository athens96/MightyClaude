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
        webShell = new WebShell(bridge, StateDirectory, options.SmokeTest, brushes.Brush(DesignToken.Page).Color);
        root.KeyboardAccelerators.Clear(); root.Children.Clear();
        Grid.SetRowSpan(webShell, 3); Grid.SetColumnSpan(webShell, 2); root.Children.Add(webShell);
    }

    /// <summary>
    /// The smoke for the web shell, in the UI smoke's place when the smoke profile holds <c>ui-web</c>: three fixture panes go
    /// into the state, and the page's DOM (read through ExecuteScriptAsync) must show the shell, every pane's title in the
    /// sidebar and the active pane in the main area; a click on the second row must make it the active pane in the host's
    /// state and in the page. No AI request is made and nothing outside the smoke profile is touched.
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
            var sessions = titles.Select((title, index) => new RunSession
            {
                WorkspaceId = workspace.Id, Title = title, Kind = index == 2 ? "shell" : "claude", Provider = index == 1 ? "codex" : "claude", Status = "completed",
                Logs = [new(Wire.Id(), "user", "fixture request " + index, Wire.Now()), new(Wire.Id(), "assistant", "fixture answer " + index, Wire.Now())],
            }).ToList();
            await service.UpdateAsync(state => state with { Sessions = sessions, ActiveWorkspaceId = workspace.Id, ActiveSessionId = sessions[0].Id });
            var shell = webShell ?? throw new InvalidOperationException("web shell missing");
            var core = await shell.Started ?? throw new InvalidOperationException("WebView2 did not start");
            const string Probe = "JSON.stringify({ mounted: !!document.querySelector('[data-testid=shell]'), titles: [...document.querySelectorAll('.sidebar-pane-title')].map(e => e.textContent), selected: document.querySelector('[data-testid=pane-title]')?.textContent ?? null, theme: document.documentElement.dataset.theme ?? null })";
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
